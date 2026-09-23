using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const long ManagementPageSize = 50;

    internal IReadOnlyList<TagMasterItem> ListTags() => ExecuteLocked(() =>
    {
        var tags = new List<TagMasterItem>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT tag.id, tag.name, COUNT(article_tag.article_id), tag.updated_at
              FROM tags tag
              LEFT JOIN article_tags article_tag ON article_tag.tag_id = tag.id
             GROUP BY tag.id, tag.name, tag.normalized_name, tag.updated_at
             ORDER BY tag.normalized_name, tag.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(new TagMasterItem(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        }
        return tags;
    });

    internal string SaveArticleCore(
        SaveArticleInput input,
        ValidatedRichContent content,
        string actorUserId,
        string articleId,
        bool isNew,
        IReadOnlyList<AttachmentRecord> attachments) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        EnsureCategoryExists(transaction, input.CategoryId);
        if (!isNew)
        {
            EnsureEditableArticle(transaction, articleId);
            using (var version = _connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = "SELECT revision FROM articles WHERE id = $id";
                version.Parameters.AddWithValue("$id", articleId);
                var revision = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                if ((SharedMode && input.ExpectedRevision is null) ||
                    (input.ExpectedRevision is not null && revision != input.ExpectedRevision))
                    throw new AppProblemException(new AppProblem("ART-010", "別の利用者または処理がこのFAQを更新しています。入力内容は保存されていません。", "入力内容を控え、最新のFAQを開き直して変更点を確認してください。"));
            }
            EnsureMergeTargetVisibility(transaction, articleId, input.Status, input.IsHidden);
            if (input.Status == ArticleStatuses.Published && string.IsNullOrWhiteSpace(input.NewBadgeUntil) &&
                RequiresNewBadgeForMergePublication(articleId, transaction))
            {
                throw new AppProblemException(new AppProblem(
                    "CDX-016", "統合FAQを初めて公開するには新着の表示終了日が必要です。",
                    "新着フラグを有効にして、表示終了日を指定してください。"));
            }
        }

        var now = UtcNow();
        if (isNew)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO articles(
                    id, category_id, title, normalized_title, summary, body_doc_json,
                    body_format_version, body_plain_text, procedure_text, caution_text,
                    status, importance, created_at, updated_at, deleted_at,
                    new_badge_until, updated_badge_until, is_hidden,
                    created_by_user_id, updated_by_user_id
                ) VALUES (
                    $id, $category_id, $title, $normalized_title, $summary, $body_doc_json,
                    2, $body_plain_text, '', '',
                    $status, $importance, $created_at, $updated_at, NULL,
                    $new_badge_until, $updated_badge_until, $is_hidden,
                    $created_by_user_id, $updated_by_user_id
                )
                """;
            AddArticleCoreParameters(insert, input, content, articleId, now, actorUserId);
            insert.Parameters.AddWithValue("$created_at", now);
            insert.Parameters.AddWithValue("$created_by_user_id", actorUserId);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE articles
                   SET category_id = $category_id,
                       title = $title,
                       normalized_title = $normalized_title,
                       summary = $summary,
                       body_doc_json = $body_doc_json,
                       body_format_version = 2,
                       body_plain_text = $body_plain_text,
                       status = $status,
                       importance = $importance,
                       new_badge_until = $new_badge_until,
                       updated_badge_until = $updated_badge_until,
                       is_hidden = $is_hidden,
                       updated_at = $updated_at,
                       updated_by_user_id = $updated_by_user_id
                 WHERE id = $id AND deleted_at IS NULL
                """;
            AddArticleCoreParameters(update, input, content, articleId, now, actorUserId);
            if (update.ExecuteNonQuery() == 0)
            {
                throw ArticleNotFoundForEditing();
            }
        }

        var tagNames = ReplaceArticleTags(transaction, articleId, input.Tags);
        ReplaceArticleAttachments(transaction, articleId, attachments);
        UpsertArticleSearchDocument(
            transaction,
            articleId,
            input.Title.Trim(),
            input.Summary.Trim(),
            content.PlainText,
            tagNames);
        RebuildArticleFts(transaction, articleId);
        transaction.Commit();
        return articleId;
    });

    internal string DuplicateArticleCore(
        string sourceArticleId,
        string actorUserId,
        string articleId,
        JsonElement bodyDocument,
        ValidatedRichContent content,
        IReadOnlyList<AttachmentRecord> attachments) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        string categoryId;
        string title;
        string summary;
        string procedureText;
        string cautionText;
        long importance;
        bool isHidden;
        using (var source = _connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = """
                SELECT category_id, title, summary, body_doc_json, body_plain_text,
                       procedure_text, caution_text, importance, is_hidden, deleted_at
                  FROM articles
                 WHERE id = $id
                """;
            source.Parameters.AddWithValue("$id", sourceArticleId);
            using var reader = source.ExecuteReader();
            if (!reader.Read())
            {
                throw ArticleNotFoundForEditing();
            }
            if (!reader.IsDBNull(9))
            {
                throw new AppProblemException(new AppProblem(
                    "ART-006",
                    "削除済みFAQは複製できません。",
                    "FAQを復元してから複製してください。"));
            }
            categoryId = reader.GetString(0);
            title = DuplicateTitle(reader.GetString(1));
            summary = reader.GetString(2);
            procedureText = reader.GetString(5);
            cautionText = reader.GetString(6);
            importance = reader.GetInt64(7);
            isHidden = reader.GetInt64(8) == 1;
        }

        var relatedIds = ListRelatedArticleIds(transaction, sourceArticleId);
        var now = UtcNow();
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO articles(
                    id, category_id, title, normalized_title, summary, body_doc_json,
                    body_format_version, body_plain_text, procedure_text, caution_text,
                    status, importance, created_at, updated_at, deleted_at,
                    new_badge_until, updated_badge_until, is_hidden,
                    created_by_user_id, updated_by_user_id
                ) VALUES (
                    $id, $category_id, $title, $normalized_title, $summary, $body_doc_json,
                    2, $body_plain_text, $procedure_text, $caution_text,
                    'draft', $importance, $now, $now, NULL,
                    NULL, NULL, $is_hidden, $actor_user_id, $actor_user_id
                )
                """;
            insert.Parameters.AddWithValue("$id", articleId);
            insert.Parameters.AddWithValue("$category_id", categoryId);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$normalized_title", NormalizeSearchText(title));
            insert.Parameters.AddWithValue("$summary", summary);
            insert.Parameters.AddWithValue("$body_doc_json", bodyDocument.GetRawText());
            insert.Parameters.AddWithValue("$body_plain_text", content.PlainText);
            insert.Parameters.AddWithValue("$procedure_text", procedureText);
            insert.Parameters.AddWithValue("$caution_text", cautionText);
            insert.Parameters.AddWithValue("$importance", importance);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$is_hidden", isHidden ? 1 : 0);
            insert.Parameters.AddWithValue("$actor_user_id", actorUserId);
            insert.ExecuteNonQuery();
        }

        foreach (var table in new[]
                 {
                     "article_symptoms", "article_causes", "article_targets",
                     "article_error_codes", "article_search_terms"
                 })
        {
            CopyArticleValues(transaction, table, sourceArticleId, articleId);
        }
        using (var tags = _connection.CreateCommand())
        {
            tags.Transaction = transaction;
            tags.CommandText = """
                INSERT INTO article_tags(article_id, tag_id)
                SELECT $target_id, tag_id FROM article_tags WHERE article_id = $source_id
                """;
            tags.Parameters.AddWithValue("$target_id", articleId);
            tags.Parameters.AddWithValue("$source_id", sourceArticleId);
            tags.ExecuteNonQuery();
        }
        ReplaceArticleAttachments(transaction, articleId, attachments);
        InsertRelatedArticleIds(transaction, articleId, relatedIds);

        using (var searchDocument = _connection.CreateCommand())
        {
            searchDocument.Transaction = transaction;
            searchDocument.CommandText = """
                INSERT INTO article_search_documents(
                    article_id, title, summary, body, symptoms, causes, targets,
                    error_codes, tags, search_terms
                )
                SELECT $target_id, $title, summary, body, symptoms, causes, targets,
                       error_codes, tags, search_terms
                  FROM article_search_documents
                 WHERE article_id = $source_id
                """;
            searchDocument.Parameters.AddWithValue("$target_id", articleId);
            searchDocument.Parameters.AddWithValue("$source_id", sourceArticleId);
            searchDocument.Parameters.AddWithValue("$title", NormalizeSearchText(title));
            if (searchDocument.ExecuteNonQuery() != 1)
            {
                throw new AppProblemException(AppProblem.Database("複製元FAQの検索情報を確認できません。"));
            }
        }
        RebuildArticleFts(transaction, articleId);
        transaction.Commit();
        return articleId;
    });

    internal ManagementArticlePage ListArticlesForManagement(ManagementArticlesInput input) => ExecuteLocked(() =>
    {
        var requestedPage = Math.Max(1, input.Page);
        var offset = checked((requestedPage - 1) * ManagementPageSize);
        var normalizedQuery = NormalizeSearchText(input.Query.Trim());
        var likeQuery = $"%{EscapeLikeForSql(normalizedQuery)}%";
        var categoryId = string.IsNullOrWhiteSpace(input.CategoryId) ? null : input.CategoryId.Trim();
        if (categoryId is not null && !CategoryExists(categoryId))
        {
            throw new AppProblemException(new AppProblem(
                "CAT-002",
                "指定した分類が見つかりません。",
                "分類一覧を更新して、選び直してください。"));
        }

        var items = new List<ManagementArticleListItem>();
        long total = 0;
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE selected_categories(id) AS (
                SELECT id FROM categories WHERE id = $category_id
                UNION ALL
                SELECT child.id FROM categories child
                JOIN selected_categories parent ON child.parent_id = parent.id
            ), filtered AS (
                SELECT article.id, article.category_id, category.name AS category_name,
                       article.title, article.summary, article.status, article.importance,
                       article.new_badge_until, article.updated_badge_until, article.is_hidden,
                       article.updated_at, article.deleted_at,
                       merge_relation.target_article_id, merge_target.title AS merge_target_title,
                       merge_relation.merged_at,
                       creator.display_name AS created_by_display_name,
                       updater.display_name AS updated_by_display_name
                  FROM articles article
                  JOIN categories category ON category.id = article.category_id
                  JOIN article_search_documents search_document ON search_document.article_id = article.id
                  LEFT JOIN article_merge_relations merge_relation
                    ON merge_relation.source_article_id = article.id
                  LEFT JOIN articles merge_target
                    ON merge_target.id = merge_relation.target_article_id
                  JOIN users creator ON creator.id = article.created_by_user_id
                  JOIN users updater ON updater.id = article.updated_by_user_id
                 WHERE (($deleted = 1 AND article.deleted_at IS NOT NULL)
                     OR ($deleted = 0 AND article.deleted_at IS NULL))
                   AND ($category_id IS NULL OR article.category_id IN (SELECT id FROM selected_categories))
                   AND ($status IS NULL OR article.status = $status)
                   AND ($query = '' OR search_document.title LIKE $like_query ESCAPE '\'
                        OR search_document.summary LIKE $like_query ESCAPE '\'
                        OR search_document.body LIKE $like_query ESCAPE '\')
            )
            SELECT id, category_id, category_name, title, summary, status, importance,
                   new_badge_until, updated_badge_until, is_hidden, updated_at, deleted_at,
                   target_article_id, merge_target_title, merged_at,
                   created_by_display_name, updated_by_display_name,
                   COUNT(*) OVER()
              FROM filtered
             ORDER BY updated_at DESC, id
             LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$query", normalizedQuery);
        command.Parameters.AddWithValue("$like_query", likeQuery);
        command.Parameters.AddWithValue("$category_id", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)input.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", input.Deleted ? 1 : 0);
        command.Parameters.AddWithValue("$limit", ManagementPageSize);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            total = reader.GetInt64(17);
            var mergeInfo = reader.IsDBNull(12)
                ? null
                : new ArticleMergeInfo(reader.GetString(12), reader.GetString(13), reader.GetString(14));
            items.Add(new ManagementArticleListItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9) == 1,
                reader.GetString(10), [], reader.GetString(15), reader.GetString(16),
                reader.IsDBNull(11) ? null : reader.GetString(11), mergeInfo));
        }
        return new ManagementArticlePage(items, total, requestedPage, ManagementPageSize);
    });

    internal void DeleteArticleCore(string id, string actorUserId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        EnsureMergeTargetVisibility(transaction, id, ArticleStatuses.Archived, false);
        var now = UtcNow();
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE articles
               SET deleted_at = $now, updated_at = $now, updated_by_user_id = $actor_user_id
             WHERE id = $id AND deleted_at IS NULL
            """;
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$actor_user_id", actorUserId);
        if (update.ExecuteNonQuery() == 0)
        {
            throw ArticleNotFoundForEditing();
        }
        DeleteArticleFts(transaction, id);
        transaction.Commit();
    });

    internal void RestoreArticleCore(string id, string actorUserId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var now = UtcNow();
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE articles
               SET deleted_at = NULL, updated_at = $now, updated_by_user_id = $actor_user_id
             WHERE id = $id AND deleted_at IS NOT NULL
            """;
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$actor_user_id", actorUserId);
        if (update.ExecuteNonQuery() == 0)
        {
            throw new AppProblemException(new AppProblem(
                "ART-005",
                "復元できる削除済みFAQが見つかりません。",
                "FAQ管理画面を更新して、削除済みFAQを選び直してください。"));
        }
        RebuildArticleFts(transaction, id);
        transaction.Commit();
    });

    internal void EnsureArticleExists(string id) => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM articles WHERE id = $id)";
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw ArticleNotFoundForEditing();
        }
    });

    internal long SearchDocumentCountForTest(string articleId) => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM article_search_documents WHERE article_id = $id";
        command.Parameters.AddWithValue("$id", articleId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    internal long FtsCountForArticleForTest(string articleId) => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM article_search_fts WHERE article_id = $id";
        command.Parameters.AddWithValue("$id", articleId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    internal string ManagementCodeForTest(string articleId) => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT management_code FROM articles WHERE id = $id";
        command.Parameters.AddWithValue("$id", articleId);
        return command.ExecuteScalar() as string ?? throw ArticleNotFoundForEditing();
    });

    private static void AddArticleCoreParameters(
        SqliteCommand command,
        SaveArticleInput input,
        ValidatedRichContent content,
        string articleId,
        string now,
        string actorUserId)
    {
        command.Parameters.AddWithValue("$id", articleId);
        command.Parameters.AddWithValue("$category_id", input.CategoryId.Trim());
        command.Parameters.AddWithValue("$title", input.Title.Trim());
        command.Parameters.AddWithValue("$normalized_title", NormalizeSearchText(input.Title.Trim()));
        command.Parameters.AddWithValue("$summary", input.Summary.Trim());
        command.Parameters.AddWithValue("$body_doc_json", input.BodyDoc.GetRawText());
        command.Parameters.AddWithValue("$body_plain_text", content.PlainText);
        command.Parameters.AddWithValue("$status", input.Status);
        command.Parameters.AddWithValue("$importance", input.Importance);
        command.Parameters.AddWithValue("$new_badge_until", (object?)input.NewBadgeUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_badge_until", (object?)input.UpdatedBadgeUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_hidden", input.IsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", now);
        command.Parameters.AddWithValue("$updated_by_user_id", actorUserId);
    }

    private static void EnsureCategoryExists(SqliteTransaction transaction, string categoryId)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM categories WHERE id = $id)";
        command.Parameters.AddWithValue("$id", categoryId);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new AppProblemException(new AppProblem(
                "ART-002",
                "選択した分類が見つかりません。",
                "分類を選び直して、もう一度保存してください。"));
        }
    }

    private void EnsureEditableArticle(SqliteTransaction transaction, string articleId)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM articles WHERE id = $id AND deleted_at IS NULL)";
        command.Parameters.AddWithValue("$id", articleId);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw ArticleNotFoundForEditing();
        }
    }

    private void EnsureMergeTargetVisibility(
        SqliteTransaction transaction,
        string articleId,
        string status,
        bool isHidden)
    {
        if (status == ArticleStatuses.Published && !isHidden)
        {
            return;
        }
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM article_merge_relations WHERE target_article_id = $id)";
        command.Parameters.AddWithValue("$id", articleId);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
        {
            throw new AppProblemException(new AppProblem(
                "ART-009",
                "統合先になっているFAQは、非表示・下書き・廃止・削除にできません。",
                "FAQ管理画面で元FAQの統合済み設定を解除してから、もう一度お試しください。"));
        }
    }

    private IReadOnlyList<string> ReplaceArticleTags(
        SqliteTransaction transaction,
        string articleId,
        IReadOnlyList<string> tags)
    {
        var resolved = new List<(string Id, string Name, string Normalized)>();
        foreach (var requestedName in tags)
        {
            var normalized = NormalizeSearchText(requestedName.Trim());
            using var tag = _connection.CreateCommand();
            tag.Transaction = transaction;
            tag.CommandText = "SELECT id, name FROM tags WHERE normalized_name = $normalized_name";
            tag.Parameters.AddWithValue("$normalized_name", normalized);
            using var reader = tag.ExecuteReader();
            if (!reader.Read())
            {
                throw new AppProblemException(new AppProblem(
                    "TAG-004",
                    "対象のタグが見つかりません。",
                    "タグマスターを再読み込みして、もう一度選択してください。"));
            }
            resolved.Add((reader.GetString(0), reader.GetString(1), normalized));
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM article_tags WHERE article_id = $article_id";
            delete.Parameters.AddWithValue("$article_id", articleId);
            delete.ExecuteNonQuery();
        }
        foreach (var tag in resolved)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO article_tags(article_id, tag_id) VALUES ($article_id, $tag_id)";
            insert.Parameters.AddWithValue("$article_id", articleId);
            insert.Parameters.AddWithValue("$tag_id", tag.Id);
            insert.ExecuteNonQuery();
        }
        return resolved.Select(tag => tag.Normalized).ToArray();
    }

    private void ReplaceArticleAttachments(
        SqliteTransaction transaction,
        string articleId,
        IReadOnlyList<AttachmentRecord> attachments)
    {
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM article_attachments WHERE article_id = $article_id";
            delete.Parameters.AddWithValue("$article_id", articleId);
            delete.ExecuteNonQuery();
        }
        foreach (var attachment in attachments)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO article_attachments(
                    id, article_id, relative_path, original_name, media_type,
                    byte_size, sha256, alt_text, created_at
                ) VALUES (
                    $id, $article_id, $relative_path, $original_name, $media_type,
                    $byte_size, $sha256, $alt_text, $created_at
                )
                """;
            insert.Parameters.AddWithValue("$id", attachment.Id);
            insert.Parameters.AddWithValue("$article_id", articleId);
            insert.Parameters.AddWithValue("$relative_path", attachment.RelativePath);
            insert.Parameters.AddWithValue("$original_name", attachment.OriginalName);
            insert.Parameters.AddWithValue("$media_type", attachment.MediaType);
            insert.Parameters.AddWithValue("$byte_size", attachment.ByteSize);
            insert.Parameters.AddWithValue("$sha256", attachment.Sha256);
            insert.Parameters.AddWithValue("$alt_text", attachment.AltText);
            insert.Parameters.AddWithValue("$created_at", attachment.CreatedAt);
            insert.ExecuteNonQuery();
        }
    }

    private void UpsertArticleSearchDocument(
        SqliteTransaction transaction,
        string articleId,
        string title,
        string summary,
        string body,
        IReadOnlyList<string> normalizedTags)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO article_search_documents(
                article_id, title, summary, body, symptoms, causes, targets,
                error_codes, tags, search_terms
            ) VALUES (
                $article_id, $title, $summary, $body, '', '', '', '', $tags, ''
            )
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title,
                summary = excluded.summary,
                body = excluded.body,
                tags = excluded.tags
            """;
        command.Parameters.AddWithValue("$article_id", articleId);
        command.Parameters.AddWithValue("$title", NormalizeSearchText(title));
        command.Parameters.AddWithValue("$summary", NormalizeSearchText(summary));
        command.Parameters.AddWithValue("$body", NormalizeSearchText(body));
        command.Parameters.AddWithValue("$tags", string.Join(' ', normalizedTags));
        command.ExecuteNonQuery();
    }

    private void RebuildArticleFts(SqliteTransaction transaction, string articleId)
    {
        DeleteArticleFts(transaction, articleId);
        string procedureText;
        string cautionText;
        using (var supplemental = _connection.CreateCommand())
        {
            supplemental.Transaction = transaction;
            supplemental.CommandText = "SELECT procedure_text, caution_text FROM articles WHERE id = $id";
            supplemental.Parameters.AddWithValue("$id", articleId);
            using var reader = supplemental.ExecuteReader();
            if (!reader.Read())
            {
                throw ArticleNotFoundForEditing();
            }
            procedureText = reader.GetString(0);
            cautionText = reader.GetString(1);
        }
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO article_search_fts(
                article_id, title, summary, body, symptoms, causes, targets,
                error_codes, tags, search_terms
            )
            SELECT article_id, title, summary, TRIM(body || ' ' || $supplemental),
                   symptoms, causes, targets, error_codes, tags, search_terms
              FROM article_search_documents
             WHERE article_id = $article_id
            """;
        insert.Parameters.AddWithValue("$article_id", articleId);
        insert.Parameters.AddWithValue(
            "$supplemental",
            $"{NormalizeSearchText(procedureText)} {NormalizeSearchText(cautionText)}".Trim());
        if (insert.ExecuteNonQuery() != 1)
        {
            throw new AppProblemException(AppProblem.Database("FAQの検索索引を更新できませんでした。"));
        }
    }

    private void DeleteArticleFts(SqliteTransaction transaction, string articleId)
    {
        using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM article_search_fts WHERE article_id = $id";
        delete.Parameters.AddWithValue("$id", articleId);
        delete.ExecuteNonQuery();
    }

    private void CopyArticleValues(
        SqliteTransaction transaction,
        string table,
        string sourceArticleId,
        string targetArticleId)
    {
        var safeTable = table switch
        {
            "article_symptoms" => "article_symptoms",
            "article_causes" => "article_causes",
            "article_targets" => "article_targets",
            "article_error_codes" => "article_error_codes",
            "article_search_terms" => "article_search_terms",
            _ => throw new InvalidOperationException("FAQ検索情報の種類が正しくありません。")
        };
        var values = new List<(string Value, string Normalized, long SortOrder)>();
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT value, normalized_value, sort_order FROM {safeTable} WHERE article_id = $id ORDER BY sort_order, id";
            read.Parameters.AddWithValue("$id", sourceArticleId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                values.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }
        foreach (var value in values)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {safeTable}(id, article_id, value, normalized_value, sort_order) VALUES ($id, $article_id, $value, $normalized_value, $sort_order)";
            insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
            insert.Parameters.AddWithValue("$article_id", targetArticleId);
            insert.Parameters.AddWithValue("$value", value.Value);
            insert.Parameters.AddWithValue("$normalized_value", value.Normalized);
            insert.Parameters.AddWithValue("$sort_order", value.SortOrder);
            insert.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<string> ListRelatedArticleIds(SqliteTransaction transaction, string articleId)
    {
        var relatedIds = new List<string>();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN source_article_id = $id THEN target_article_id ELSE source_article_id END
              FROM article_relations
             WHERE source_article_id = $id OR target_article_id = $id
             ORDER BY sort_order, source_article_id, target_article_id
            """;
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            relatedIds.Add(reader.GetString(0));
        }
        return relatedIds;
    }

    private void InsertRelatedArticleIds(
        SqliteTransaction transaction,
        string articleId,
        IReadOnlyList<string> relatedIds)
    {
        for (var index = 0; index < relatedIds.Count; index++)
        {
            var relatedId = relatedIds[index];
            var source = string.CompareOrdinal(articleId, relatedId) < 0 ? articleId : relatedId;
            var target = string.CompareOrdinal(articleId, relatedId) < 0 ? relatedId : articleId;
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO article_relations(source_article_id, target_article_id, sort_order) VALUES ($source, $target, $sort_order)";
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$target", target);
            insert.Parameters.AddWithValue("$sort_order", index);
            insert.ExecuteNonQuery();
        }
    }

    private static string DuplicateTitle(string source)
    {
        const string suffix = "（コピー）";
        var maximumSourceRunes = 200 - suffix.EnumerateRunes().Count();
        return string.Concat(source.EnumerateRunes().Take(maximumSourceRunes).Select(rune => rune.ToString())) + suffix;
    }

    private static string EscapeLikeForSql(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static AppProblemException ArticleNotFoundForEditing() => new(new AppProblem(
        "ART-004",
        "指定したFAQが見つかりません。",
        "一覧を更新して、FAQを選び直してください。"));
}

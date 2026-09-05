using System.Globalization;
using System.Text.Json;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    internal ArticleDetail GetArticle(string id) => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT article.id, article.category_id, category.name, article.title, article.summary,
                   article.body_doc_json, article.body_plain_text, article.status, article.importance,
                   article.created_at, article.updated_at, article.deleted_at,
                   article.new_badge_until, article.updated_badge_until, article.is_hidden,
                   merge_relation.target_article_id, merge_target.title, merge_relation.merged_at,
                   article.created_by_user_id, creator.display_name,
                   article.updated_by_user_id, updater.display_name
              FROM articles article
              JOIN categories category ON category.id = article.category_id
              LEFT JOIN article_merge_relations merge_relation
                ON merge_relation.source_article_id = article.id
              LEFT JOIN articles merge_target
                ON merge_target.id = merge_relation.target_article_id
              JOIN users creator ON creator.id = article.created_by_user_id
              JOIN users updater ON updater.id = article.updated_by_user_id
             WHERE article.id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw ArticleNotFound();
        }

        JsonElement bodyDoc;
        try
        {
            using var document = JsonDocument.Parse(reader.GetString(5),
                new JsonDocumentOptions { MaxDepth = SafeRichContentValidator.MaximumStoredJsonDepth });
            bodyDoc = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AppProblemException(AppProblem.Database("FAQ本文の保存形式を確認できません。"));
        }

        var mergeInfo = reader.IsDBNull(15)
            ? null
            : new ArticleMergeInfo(reader.GetString(15), reader.GetString(16), reader.GetString(17));
        var article = new ArticleDetail(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            bodyDoc,
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetInt64(14) == 1,
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            mergeInfo,
            [], [], [], [], [], [], [], [], [], []);
        reader.Close();

        var proceduresAndCautions = ReadProceduresAndCautions(id);
        return article with
        {
            Attachments = ListArticleAttachments(id),
            Symptoms = ListArticleValues("article_symptoms", id),
            Causes = ListArticleValues("article_causes", id),
            Targets = ListArticleValues("article_targets", id),
            ErrorCodes = ListArticleValues("article_error_codes", id),
            Procedures = SplitStoredValues(proceduresAndCautions.Procedures),
            Cautions = SplitStoredValues(proceduresAndCautions.Cautions),
            Tags = ListArticleTags(id),
            SearchTerms = ListArticleValues("article_search_terms", id),
            RelatedArticles = ListRelatedArticles(id)
        };
    });

    internal void RecordArticleView(string articleId, string? sourceSearchLogId) => ExecuteLocked(() =>
    {
        using (var article = _connection.CreateCommand())
        {
            article.CommandText = "SELECT EXISTS(SELECT 1 FROM articles WHERE id = $id)";
            article.Parameters.AddWithValue("$id", articleId);
            if (Convert.ToInt64(article.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw ArticleNotFound();
            }
        }
        if (!string.IsNullOrWhiteSpace(sourceSearchLogId))
        {
            using var searchLog = _connection.CreateCommand();
            searchLog.CommandText = "SELECT EXISTS(SELECT 1 FROM search_logs WHERE id = $id)";
            searchLog.Parameters.AddWithValue("$id", sourceSearchLogId);
            if (Convert.ToInt64(searchLog.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new AppProblemException(new AppProblem(
                    "LOG-001",
                    "検索履歴との関連を確認できませんでした。",
                    "FAQ一覧へ戻り、検索結果からもう一度FAQを開いてください。"));
            }
        }

        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO view_logs(id, article_id, source_search_log_id, viewed_at)
            VALUES ($id, $article_id, $source_search_log_id, $viewed_at)
            """;
        insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        insert.Parameters.AddWithValue("$article_id", articleId);
        insert.Parameters.AddWithValue(
            "$source_search_log_id",
            string.IsNullOrWhiteSpace(sourceSearchLogId) ? DBNull.Value : sourceSearchLogId);
        insert.Parameters.AddWithValue("$viewed_at", UtcNow());
        insert.ExecuteNonQuery();
    });

    internal long ViewLogCountForTest() => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM view_logs";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    internal ViewLogSnapshot LastViewLogForTest() => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT article_id, source_search_log_id
              FROM view_logs
             ORDER BY viewed_at DESC, id DESC
             LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("閲覧履歴がありません。");
        }
        return new ViewLogSnapshot(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    });

    private (string Procedures, string Cautions) ReadProceduresAndCautions(string articleId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT procedure_text, caution_text FROM articles WHERE id = $id";
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw ArticleNotFound();
        }
        return (reader.GetString(0), reader.GetString(1));
    }

    private IReadOnlyList<string> ListArticleValues(string table, string articleId)
    {
        var safeTable = table switch
        {
            "article_symptoms" => "article_symptoms",
            "article_causes" => "article_causes",
            "article_targets" => "article_targets",
            "article_error_codes" => "article_error_codes",
            "article_search_terms" => "article_search_terms",
            _ => throw new AppProblemException(AppProblem.Database("FAQ検索情報の種類が正しくありません。"))
        };
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {safeTable} WHERE article_id = $id ORDER BY sort_order, id";
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private IReadOnlyList<string> ListArticleTags(string articleId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT tag.name
              FROM article_tags article_tag
              JOIN tags tag ON tag.id = article_tag.tag_id
             WHERE article_tag.article_id = $id
             ORDER BY tag.normalized_name, tag.id
            """;
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }
        return tags;
    }

    private IReadOnlyList<ArticleAttachmentSummary> ListArticleAttachments(string articleId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, original_name, media_type, byte_size, sha256, alt_text, relative_path, created_at
              FROM article_attachments
             WHERE article_id = $id
             ORDER BY created_at, id
            """;
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        var attachments = new List<ArticleAttachmentSummary>();
        while (reader.Read())
        {
            attachments.Add(new ArticleAttachmentSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }
        return attachments;
    }

    private IReadOnlyList<RelatedArticleSummary> ListRelatedArticles(string articleId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT related.id, related.title, related.status, related.deleted_at,
                   EXISTS(
                       SELECT 1 FROM article_merge_relations merge_relation
                        WHERE merge_relation.source_article_id = related.id
                   )
              FROM article_relations relation
              JOIN articles related
                ON related.id = CASE
                    WHEN relation.source_article_id = $id THEN relation.target_article_id
                    ELSE relation.source_article_id
                END
             WHERE relation.source_article_id = $id OR relation.target_article_id = $id
             ORDER BY related.deleted_at IS NOT NULL, related.title, related.id
            """;
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        var related = new List<RelatedArticleSummary>();
        while (reader.Read())
        {
            related.Add(new RelatedArticleSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) == 1));
        }
        return related;
    }

    private static AppProblemException ArticleNotFound() => new(new AppProblem(
        "ART-004",
        "指定したFAQが見つかりません。",
        "一覧を更新して、FAQを選び直してください。"));
}

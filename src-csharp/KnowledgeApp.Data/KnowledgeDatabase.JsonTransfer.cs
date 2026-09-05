using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const int MaximumJsonBytes = 100 * 1024 * 1024;
    private static readonly Regex Rfc3339Pattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions JsonTransferOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = StructuredJsonBoundary.TransferDepth,
        WriteIndented = true
    };

    internal JsonExportResult ExportJson(string destination) => ExecuteLocked(() =>
    {
        var document = BuildJsonDocument();
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonTransferOptions);
            PublishTransferFile(destination, bytes, "JSON");
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw JsonWriteProblem();
        }
        return new JsonExportResult(destination, JsonCounts(document));
    });

    internal JsonImportPreview InspectJson(string source) => ExecuteLocked(() =>
    {
        var (fileSha256, document) = ReadJsonDocument(source);
        return InspectJsonSnapshot(source, fileSha256, document);
    });

    // Isolated regression seam only. No host command exposes callbacks or paths.
    internal Action? JsonImportSnapshotValidatedForTest { get; set; }

    internal JsonImportResult ImportJson(
        string source,
        string expectedFileSha256,
        string actorUserId,
        string safetyBackupPath) => ExecuteLocked(() =>
    {
        // Hash, validation, counts and writes must use this one immutable file
        // snapshot. Reopening the path after validation can import another file.
        var (fileSha256, document) = ReadJsonDocument(source);
        var preview = InspectJsonSnapshot(source, fileSha256, document);
        if (!string.Equals(preview.FileSha256, expectedFileSha256, StringComparison.Ordinal))
        {
            throw new AppProblemException(new AppProblem(
                "JSON-007",
                "確認後にJSONファイルが変更されています。",
                "JSONをもう一度プレビューしてから取り込んでください。"));
        }
        if (preview.ErrorCount > 0)
        {
            throw new AppProblemException(new AppProblem(
                "JSON-004",
                "エラーがあるためJSONを取り込めません。",
                "プレビューに表示された内容を修正し、もう一度選択してください。"));
        }
        JsonImportSnapshotValidatedForTest?.Invoke();
        var depths = JsonCategoryDepths(document, []);
        using var transaction = _connection.BeginTransaction();

        TemporarilyRenameImportedValues(transaction, "categories", "normalized_name", document.Categories.Select(item => item.Id));
        foreach (var category in document.Categories.OrderBy(item => depths.GetValueOrDefault(item.Id, 6)))
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO categories(
                    id, management_code, parent_id, name, normalized_name, description,
                    depth, sort_order, created_at, updated_at
                ) VALUES (
                    $id, $management_code, $parent_id, $name, $normalized_name, $description,
                    $depth, $sort_order, $created_at, $updated_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    parent_id = excluded.parent_id, name = excluded.name,
                    normalized_name = excluded.normalized_name, description = excluded.description,
                    depth = excluded.depth, sort_order = excluded.sort_order,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", category.Id);
            command.Parameters.AddWithValue("$management_code", category.ManagementCode.ToUpperInvariant());
            command.Parameters.AddWithValue("$parent_id", (object?)category.ParentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", category.Name.Trim());
            command.Parameters.AddWithValue("$normalized_name", NormalizeSearchText(category.Name));
            command.Parameters.AddWithValue("$description", category.Description.Trim());
            command.Parameters.AddWithValue("$depth", depths.GetValueOrDefault(category.Id, 1));
            command.Parameters.AddWithValue("$sort_order", category.SortOrder);
            command.Parameters.AddWithValue("$created_at", category.CreatedAt);
            command.Parameters.AddWithValue("$updated_at", category.UpdatedAt);
            command.ExecuteNonQuery();
        }

        TemporarilyRenameImportedValues(transaction, "tags", "normalized_name", document.Tags.Select(item => item.Id));
        var now = UtcNow();
        foreach (var tag in document.Tags)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tags(id, name, normalized_name, created_at, updated_at)
                VALUES ($id, $name, $normalized_name, $now, $now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, normalized_name = excluded.normalized_name,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", tag.Id);
            command.Parameters.AddWithValue("$name", tag.Name.Trim());
            command.Parameters.AddWithValue("$normalized_name", NormalizeSearchText(tag.Name));
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        foreach (var group in document.SynonymGroups)
        {
            var values = ValidateSynonymValues(group.DisplayName, group.Terms);
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO synonym_groups(id, display_name, created_at, updated_at)
                    VALUES ($id, $display_name, $now, $now)
                    ON CONFLICT(id) DO UPDATE SET
                        display_name = excluded.display_name, updated_at = excluded.updated_at
                    """;
                command.Parameters.AddWithValue("$id", group.Id);
                command.Parameters.AddWithValue("$display_name", group.DisplayName.Trim());
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }
            ExecuteTransferDelete(transaction, "DELETE FROM synonyms WHERE group_id = $id", group.Id);
            foreach (var (term, normalized) in values)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO synonyms(id, group_id, term, normalized_term) VALUES ($id, $group_id, $term, $normalized_term)";
                insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
                insert.Parameters.AddWithValue("$group_id", group.Id);
                insert.Parameters.AddWithValue("$term", term);
                insert.Parameters.AddWithValue("$normalized_term", normalized);
                insert.ExecuteNonQuery();
            }
        }

        foreach (var article in document.Articles)
        {
            var content = SafeRichContentValidator.Validate(article.BodyDoc);
            if (content.Attachments.Count > 0)
            {
                throw JsonImportProblem("画像参照を含むJSONは取り込めません。");
            }
            var exists = TransferRowExists(transaction, "articles", article.Id);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            if (exists)
            {
                command.CommandText = """
                    UPDATE articles
                       SET category_id = $category_id, title = $title,
                           normalized_title = $normalized_title, summary = $summary,
                           body_doc_json = $body_doc_json, body_format_version = 2,
                           body_plain_text = $body_plain_text, status = $status,
                           importance = $importance, new_badge_until = $new_badge_until,
                           updated_badge_until = $updated_badge_until, is_hidden = $is_hidden,
                           updated_at = $updated_at, deleted_at = $deleted_at,
                           updated_by_user_id = $actor
                     WHERE id = $id
                    """;
            }
            else
            {
                command.CommandText = """
                    INSERT INTO articles(
                        id, management_code, category_id, title, normalized_title, summary,
                        body_doc_json, body_format_version, body_plain_text, status, importance,
                        new_badge_until, updated_badge_until, is_hidden, created_at, updated_at,
                        deleted_at, created_by_user_id, updated_by_user_id
                    ) VALUES (
                        $id, $management_code, $category_id, $title, $normalized_title, $summary,
                        $body_doc_json, 2, $body_plain_text, $status, $importance,
                        $new_badge_until, $updated_badge_until, $is_hidden, $created_at, $updated_at,
                        $deleted_at, $actor, $actor
                    )
                    """;
                command.Parameters.AddWithValue("$management_code", article.ManagementCode.ToUpperInvariant());
                command.Parameters.AddWithValue("$created_at", article.CreatedAt);
            }
            AddJsonArticleParameters(command, article, content.PlainText, actorUserId);
            command.ExecuteNonQuery();
            ReplaceJsonArticleDetails(transaction, article);
            UpsertJsonSearchDocument(transaction, article, content.PlainText);
            RebuildArticleFts(transaction, article.Id);
        }

        foreach (var relation in document.Relations)
        {
            var sourceId = string.CompareOrdinal(relation.SourceArticleId, relation.TargetArticleId) < 0
                ? relation.SourceArticleId : relation.TargetArticleId;
            var targetId = sourceId == relation.SourceArticleId
                ? relation.TargetArticleId : relation.SourceArticleId;
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO article_relations(source_article_id, target_article_id, sort_order)
                VALUES ($source_id, $target_id, $sort_order)
                ON CONFLICT(source_article_id, target_article_id) DO UPDATE SET
                    sort_order = excluded.sort_order
                """;
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.Parameters.AddWithValue("$target_id", targetId);
            command.Parameters.AddWithValue("$sort_order", relation.SortOrder);
            command.ExecuteNonQuery();
        }
        foreach (var article in document.Articles)
        {
            ExecuteTransferDelete(transaction, "DELETE FROM article_merge_relations WHERE source_article_id = $id", article.Id);
        }
        foreach (var relation in document.MergeRelations)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO article_merge_relations(
                    source_article_id, target_article_id, source_updated_at, merged_at
                ) VALUES ($source_id, $target_id, $source_updated_at, $merged_at)
                """;
            command.Parameters.AddWithValue("$source_id", relation.SourceArticleId);
            command.Parameters.AddWithValue("$target_id", relation.TargetArticleId);
            command.Parameters.AddWithValue("$source_updated_at", relation.SourceUpdatedAt);
            command.Parameters.AddWithValue("$merged_at", relation.MergedAt);
            command.ExecuteNonQuery();
        }
        UpdateManagementSequence(transaction, "category", "categories");
        UpdateManagementSequence(transaction, "article", "articles");
        transaction.Commit();
        return new JsonImportResult(
            source,
            safetyBackupPath,
            preview.CreateCount,
            preview.UpdateCount,
            preview.UnchangedCount);
    });

    private JsonImportPreview InspectJsonSnapshot(string source, string fileSha256, KnowledgeJsonDocument document)
    {
        if (document.FormatVersion != 1)
        {
            throw new AppProblemException(new AppProblem(
                "JSON-003",
                $"JSON形式版{document.FormatVersion}には対応していません。",
                "このバージョンのKnowledgeAppから書き出した形式版1のファイルを使用してください。"));
        }
        var errors = ValidateJsonDocument(document);
        var counts = JsonActionCounts(document, BuildJsonDocument());
        return new JsonImportPreview(
            source, fileSha256, JsonCounts(document), counts.Create, counts.Update,
            counts.Unchanged, errors.Count, errors);
    }

    private KnowledgeJsonDocument BuildJsonDocument()
    {
        var categories = new List<JsonCategoryData>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, management_code, parent_id, name, description, sort_order,
                       created_at, updated_at
                  FROM categories
                 ORDER BY depth, sort_order, id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                categories.Add(new JsonCategoryData
                {
                    Id = reader.GetString(0), ManagementCode = reader.GetString(1),
                    ParentId = reader.IsDBNull(2) ? null : reader.GetString(2), Name = reader.GetString(3),
                    Description = reader.GetString(4), SortOrder = reader.GetInt64(5),
                    CreatedAt = reader.GetString(6), UpdatedAt = reader.GetString(7)
                });
            }
        }
        var tags = new List<JsonTagData>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name FROM tags ORDER BY normalized_name, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tags.Add(new JsonTagData { Id = reader.GetString(0), Name = reader.GetString(1) });
            }
        }
        var synonyms = ListSynonymGroupsUnlocked().Select(group => new JsonSynonymGroupData
        {
            Id = group.Id,
            DisplayName = group.DisplayName,
            Terms = group.Terms
        }).ToArray();

        var baseRows = new List<JsonArticleBaseRow>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, management_code, category_id, title, summary, body_doc_json,
                       status, importance, new_badge_until, updated_badge_until, is_hidden,
                       created_at, updated_at, deleted_at, procedure_text, caution_text
                  FROM articles
                 ORDER BY created_at, management_code, id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                baseRows.Add(new JsonArticleBaseRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10) == 1,
                    reader.GetString(11), reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.GetString(14), reader.GetString(15)));
            }
        }
        var articles = new List<JsonArticleData>();
        foreach (var row in baseRows)
        {
            JsonElement body;
            try
            {
                using var document = JsonDocument.Parse(row.BodyDocumentJson, StructuredJsonBoundary.DocumentOptions);
                body = SafeRichContentValidator.WithoutImages(document.RootElement);
            }
            catch (JsonException)
            {
                throw new AppProblemException(AppProblem.Database("FAQ回答の保存形式を読み取れませんでした。"));
            }
            articles.Add(new JsonArticleData
            {
                Id = row.Id, ManagementCode = row.ManagementCode, CategoryId = row.CategoryId,
                Title = row.Title, Summary = row.Summary, BodyDoc = body, Status = row.Status,
                Importance = row.Importance, NewBadgeUntil = row.NewBadgeUntil,
                UpdatedBadgeUntil = row.UpdatedBadgeUntil, IsHidden = row.IsHidden,
                CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt, DeletedAt = row.DeletedAt,
                Symptoms = ReadDetailValues("article_symptoms", row.Id),
                Causes = ReadDetailValues("article_causes", row.Id),
                Targets = ReadDetailValues("article_targets", row.Id),
                ErrorCodes = ReadDetailValues("article_error_codes", row.Id),
                Procedures = SplitTransferStoredValues(row.ProcedureText),
                Cautions = SplitTransferStoredValues(row.CautionText),
                TagIds = ReadArticleTagIds(row.Id),
                SearchTerms = ReadDetailValues("article_search_terms", row.Id)
            });
        }

        var relations = new List<JsonArticleRelationData>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT source_article_id, target_article_id, sort_order FROM article_relations ORDER BY source_article_id, target_article_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                relations.Add(new JsonArticleRelationData
                {
                    SourceArticleId = reader.GetString(0), TargetArticleId = reader.GetString(1),
                    SortOrder = reader.GetInt64(2)
                });
            }
        }
        var mergeRelations = new List<JsonMergeRelationData>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT source_article_id, target_article_id, source_updated_at, merged_at FROM article_merge_relations ORDER BY source_article_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                mergeRelations.Add(new JsonMergeRelationData
                {
                    SourceArticleId = reader.GetString(0), TargetArticleId = reader.GetString(1),
                    SourceUpdatedAt = reader.GetString(2), MergedAt = reader.GetString(3)
                });
            }
        }
        return new KnowledgeJsonDocument
        {
            FormatVersion = 1,
            ExportedAt = UtcNow(),
            Categories = categories,
            Tags = tags,
            SynonymGroups = synonyms,
            Articles = articles,
            Relations = relations,
            MergeRelations = mergeRelations
        };
    }

    private (string Hash, KnowledgeJsonDocument Document) ReadJsonDocument(string source)
    {
        byte[] bytes;
        try
        {
            bytes = FileSystemBoundary.ReadBoundedFile(source, MaximumJsonBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw JsonReadProblem("JSONファイルを読み込めませんでした。");
        }
        if (bytes.Length > MaximumJsonBytes)
        {
            throw JsonReadProblem("JSONファイルが100MBを超えています。");
        }
        try
        {
            using var parsed = JsonDocument.Parse(bytes, StructuredJsonBoundary.TransferOptions);
            StructuredJsonBoundary.Validate(parsed.RootElement, StructuredJsonBoundary.TransferDepth);
            var document = parsed.RootElement.Deserialize<KnowledgeJsonDocument>(JsonTransferOptions)
                ?? throw new JsonException();
            if (document.Categories is null || document.Tags is null || document.SynonymGroups is null ||
                document.Articles is null || document.Relations is null || document.MergeRelations is null ||
                document.ExportedAt is null)
            {
                throw new JsonException();
            }
            return (Convert.ToHexStringLower(SHA256.HashData(bytes)), document);
        }
        catch (JsonException)
        {
            throw JsonReadProblem("JSONの構造、文字コード、または必須項目が正しくありません。");
        }
    }

    private IReadOnlyList<string> ValidateJsonDocument(KnowledgeJsonDocument document)
    {
        var errors = new List<string>();
        if (!ValidRfc3339(document.ExportedAt))
        {
            errors.Add("exportedAtはRFC 3339形式の日時にしてください。");
        }
        ValidateUniqueIds(document.Categories.Select(item => item.Id), "分類", errors);
        ValidateUniqueIds(document.Articles.Select(item => item.Id), "FAQ", errors);
        ValidateUniqueIds(document.Tags.Select(item => item.Id), "タグ", errors);
        ValidateUniqueIds(document.SynonymGroups.Select(item => item.Id), "同義語グループ", errors);

        var categoryIds = document.Categories.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var articleIds = document.Articles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var tagIds = document.Tags.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var depths = JsonCategoryDepths(document, errors);
        var siblingNames = new HashSet<(string? ParentId, string Name)>();
        var managementCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in document.Categories)
        {
            var validText = true;
            try
            {
                _ = ValidateCategoryName(category.Name);
                _ = ValidateCategoryDescription(category.Description);
            }
            catch (AppProblemException)
            {
                validText = false;
            }
            if (!Guid.TryParse(category.Id, out _))
            {
                errors.Add($"分類「{category.Name}」のIDがUUIDではありません。");
            }
            if (!ValidManagementCode(category.ManagementCode, "CAT-") || !managementCodes.Add(category.ManagementCode))
            {
                errors.Add($"分類「{category.Name}」の管理IDが不正または重複しています。");
            }
            if (!validText)
            {
                errors.Add($"分類「{category.Name}」の名前または説明が不正です。");
            }
            if (category.SortOrder < 0)
            {
                errors.Add($"分類「{category.Name}」の並び順は0以上にしてください。");
            }
            if (category.ParentId is not null && !categoryIds.Contains(category.ParentId))
            {
                errors.Add($"分類「{category.Name}」の親分類がJSON内にありません。");
            }
            if (!ValidRfc3339(category.CreatedAt) || !ValidRfc3339(category.UpdatedAt))
            {
                errors.Add($"分類「{category.Name}」の日時が不正です。");
            }
            if (!siblingNames.Add((category.ParentId, NormalizeSearchText(category.Name))))
            {
                errors.Add($"同じ親の下に分類「{category.Name}」が重複しています。");
            }
            if (depths.GetValueOrDefault(category.Id, 6) > 5)
            {
                errors.Add($"分類「{category.Name}」が5階層を超えています。");
            }
        }

        var tagNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in document.Tags)
        {
            if (!Guid.TryParse(tag.Id, out _) || !ValidSingleLine(tag.Name.Trim(), 100) ||
                !tagNames.Add(NormalizeSearchText(tag.Name)))
            {
                errors.Add($"タグ「{tag.Name}」が不正または重複しています。");
            }
        }
        var synonymOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in document.SynonymGroups)
        {
            if (!Guid.TryParse(group.Id, out _))
            {
                errors.Add($"同義語グループ「{group.DisplayName}」のIDがUUIDではありません。");
            }
            try
            {
                foreach (var (_, normalized) in ValidateSynonymValues(group.DisplayName, group.Terms))
                {
                    if (synonymOwners.TryGetValue(normalized, out var owner) && owner != group.Id)
                    {
                        errors.Add($"同じ同義語が複数のグループに登録されています（{group.DisplayName}）。");
                    }
                    synonymOwners[normalized] = group.Id;
                }
            }
            catch (AppProblemException exception)
            {
                errors.Add($"同義語グループ「{group.DisplayName}」: {exception.Problem.Message}");
            }
        }

        foreach (var article in document.Articles)
        {
            if (!Guid.TryParse(article.Id, out _))
            {
                errors.Add($"FAQ「{article.Title}」のIDがUUIDではありません。");
            }
            if (!ValidManagementCode(article.ManagementCode, "FAQ-") || !managementCodes.Add(article.ManagementCode))
            {
                errors.Add($"FAQ「{article.Title}」の管理IDが不正または重複しています。");
            }
            if (!categoryIds.Contains(article.CategoryId))
            {
                errors.Add($"FAQ「{article.Title}」の分類がJSON内にありません。");
            }
            if (article.Title.Trim().EnumerateRunes().Count() is < 1 or > 200 ||
                article.Summary.EnumerateRunes().Count() > 500 ||
                !ArticleStatuses.IsValid(article.Status) || article.Importance is < 1 or > 3)
            {
                errors.Add($"FAQ「{article.Title}」の基本項目が不正です。");
            }
            try
            {
                var content = SafeRichContentValidator.Validate(article.BodyDoc);
                if (content.Attachments.Count > 0)
                {
                    errors.Add($"FAQ「{article.Title}」に画像参照があります。JSONには画像を含められません。");
                }
                else if (article.Status == ArticleStatuses.Published &&
                    (string.IsNullOrWhiteSpace(article.Summary) || string.IsNullOrWhiteSpace(content.PlainText)))
                {
                    errors.Add($"公開FAQ「{article.Title}」には概要と画像以外の回答本文が必要です。");
                }
            }
            catch (AppProblemException exception)
            {
                errors.Add($"FAQ「{article.Title}」の回答形式が安全ではありません（{exception.Problem.Message}）。");
            }
            if (!ValidDate(article.NewBadgeUntil) || !ValidDate(article.UpdatedBadgeUntil) ||
                !ValidRfc3339(article.CreatedAt) || !ValidRfc3339(article.UpdatedAt) ||
                article.DeletedAt is not null && !ValidRfc3339(article.DeletedAt))
            {
                errors.Add($"FAQ「{article.Title}」の日付または日時が不正です。");
            }
            if (article.TagIds.Any(tagId => !tagIds.Contains(tagId)))
            {
                errors.Add($"FAQ「{article.Title}」に未定義のタグIDがあります。");
            }
            if (!ValidJsonArticleDetails(article))
            {
                errors.Add($"FAQ「{article.Title}」の検索情報が不正です。");
            }
        }

        var relationKeys = new HashSet<(string Source, string Target)>();
        foreach (var relation in document.Relations)
        {
            var key = string.CompareOrdinal(relation.SourceArticleId, relation.TargetArticleId) < 0
                ? (relation.SourceArticleId, relation.TargetArticleId)
                : (relation.TargetArticleId, relation.SourceArticleId);
            if (relation.SourceArticleId == relation.TargetArticleId ||
                !articleIds.Contains(relation.SourceArticleId) || !articleIds.Contains(relation.TargetArticleId) ||
                !relationKeys.Add(key))
            {
                errors.Add("関連FAQに自己参照、未定義ID、または重複があります。");
            }
        }
        var mergeSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in document.MergeRelations)
        {
            if (relation.SourceArticleId == relation.TargetArticleId ||
                !articleIds.Contains(relation.SourceArticleId) || !articleIds.Contains(relation.TargetArticleId) ||
                !mergeSources.Add(relation.SourceArticleId) || !ValidRfc3339(relation.SourceUpdatedAt) ||
                !ValidRfc3339(relation.MergedAt))
            {
                errors.Add("統合関係に自己参照、未定義ID、重複、または不正日時があります。");
            }
        }
        ValidateJsonDatabaseCollisions(document, errors);
        return errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private void ValidateJsonDatabaseCollisions(KnowledgeJsonDocument document, List<string> errors)
    {
        var categories = new Dictionary<string, (string? ParentId, string Name, string Code)>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, parent_id, normalized_name, management_code FROM categories";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                categories[reader.GetString(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3));
            }
        }
        foreach (var category in document.Categories)
        {
            if (categories.TryGetValue(category.Id, out var current) &&
                !string.Equals(current.Code, category.ManagementCode, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"分類ID {category.Id} は別の管理IDで登録済みです。");
            }
            categories[category.Id] = (category.ParentId, NormalizeSearchText(category.Name), category.ManagementCode.ToUpperInvariant());
        }
        if (categories.Values.GroupBy(value => (value.ParentId, value.Name)).Any(group => group.Count() > 1))
        {
            errors.Add("取込後に同じ親の下で分類名が重複します。");
        }
        if (categories.Values.GroupBy(value => value.Code, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            errors.Add("分類管理IDが既存分類と衝突します。");
        }

        var articleCodes = ReadIdValueMap("articles", "management_code");
        foreach (var article in document.Articles)
        {
            if (articleCodes.TryGetValue(article.Id, out var currentCode) &&
                !string.Equals(currentCode, article.ManagementCode, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"FAQ ID {article.Id} は別の管理IDで登録済みです。");
            }
            articleCodes[article.Id] = article.ManagementCode.ToUpperInvariant();
        }
        if (articleCodes.Values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            errors.Add("FAQ管理IDが既存FAQと衝突します。");
        }

        var tagNames = ReadIdValueMap("tags", "normalized_name");
        foreach (var tag in document.Tags)
        {
            tagNames[tag.Id] = NormalizeSearchText(tag.Name);
        }
        if (tagNames.Values.GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            errors.Add("タグ名が既存タグと衝突します。");
        }

        var importedGroups = document.SynonymGroups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var termOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT group_id, normalized_term FROM synonyms";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!importedGroups.Contains(reader.GetString(0)))
                {
                    termOwners[reader.GetString(1)] = reader.GetString(0);
                }
            }
        }
        foreach (var group in document.SynonymGroups)
        {
            try
            {
                foreach (var (_, normalized) in ValidateSynonymValues(group.DisplayName, group.Terms))
                {
                    if (termOwners.ContainsKey(normalized))
                    {
                        errors.Add("同義語が既存の別グループと衝突します。");
                    }
                    termOwners[normalized] = group.Id;
                }
            }
            catch (AppProblemException)
            {
                // 文書内検査ですでに利用者向けエラーへ変換済み。
            }
        }
    }

    private IReadOnlyList<string> ReadDetailValues(string table, string articleId)
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
        var values = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {safeTable} WHERE article_id = $article_id ORDER BY sort_order, id";
        command.Parameters.AddWithValue("$article_id", articleId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private IReadOnlyList<string> ReadArticleTagIds(string articleId)
    {
        var values = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT tag_id FROM article_tags WHERE article_id = $article_id ORDER BY tag_id";
        command.Parameters.AddWithValue("$article_id", articleId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static IReadOnlyList<string> SplitTransferStoredValues(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0)
        .ToArray();

    private static JsonEntityCounts JsonCounts(KnowledgeJsonDocument document) => new(
        document.Categories.Count,
        document.Articles.Count,
        document.Tags.Count,
        document.SynonymGroups.Count,
        document.Relations.Count,
        document.MergeRelations.Count);

    private static (long Create, long Update, long Unchanged) JsonActionCounts(
        KnowledgeJsonDocument incoming,
        KnowledgeJsonDocument current)
    {
        var groups = new[]
        {
            CountJsonActions(incoming.Categories, current.Categories, item => item.Id),
            CountJsonActions(incoming.Articles, current.Articles, item => item.Id),
            CountJsonActions(incoming.Tags, current.Tags, item => item.Id),
            CountJsonActions(incoming.SynonymGroups, current.SynonymGroups, item => item.Id),
            CountJsonActions(incoming.Relations, current.Relations, item => $"{item.SourceArticleId}:{item.TargetArticleId}"),
            CountJsonActions(incoming.MergeRelations, current.MergeRelations, item => item.SourceArticleId)
        };
        return (
            groups.Sum(group => group.Create),
            groups.Sum(group => group.Update),
            groups.Sum(group => group.Unchanged));
    }

    private static (long Create, long Update, long Unchanged) CountJsonActions<T>(
        IReadOnlyList<T> incoming,
        IReadOnlyList<T> current,
        Func<T, string> key)
    {
        var currentItems = current.ToDictionary(key, item => JsonSerializer.Serialize(item, JsonTransferOptions), StringComparer.Ordinal);
        long create = 0;
        long update = 0;
        long unchanged = 0;
        foreach (var item in incoming)
        {
            if (!currentItems.TryGetValue(key(item), out var currentJson))
            {
                create++;
            }
            else if (currentJson == JsonSerializer.Serialize(item, JsonTransferOptions))
            {
                unchanged++;
            }
            else
            {
                update++;
            }
        }
        return (create, update, unchanged);
    }

    private static Dictionary<string, long> JsonCategoryDepths(
        KnowledgeJsonDocument document,
        ICollection<string> errors)
    {
        var parents = document.Categories.ToDictionary(item => item.Id, item => item.ParentId, StringComparer.Ordinal);
        var depths = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var category in document.Categories)
        {
            long depth = 1;
            var current = category.ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { category.Id };
            while (current is not null)
            {
                if (!visited.Add(current))
                {
                    errors.Add($"分類「{category.Name}」の階層が循環しています。");
                    depth = 6;
                    break;
                }
                depth++;
                current = parents.GetValueOrDefault(current);
                if (depth > 5)
                {
                    break;
                }
            }
            depths[category.Id] = depth;
        }
        return depths;
    }

    private static void ValidateUniqueIds(IEnumerable<string> ids, string label, ICollection<string> errors)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!unique.Add(id))
            {
                errors.Add($"同じ{label}IDがJSON内に複数あります（{id}）。");
            }
        }
    }

    private static bool ValidManagementCode(string value, string prefix)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var number = value[prefix.Length..];
        return number.Length >= 5 && number.All(char.IsAsciiDigit);
    }

    private static bool ValidRfc3339(string value) =>
        Rfc3339Pattern.IsMatch(value) && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _);

    private static bool ValidDate(string? value) => value is null || DateOnly.TryParseExact(
        value,
        "yyyy-MM-dd",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out _);

    private static bool ValidJsonArticleDetails(JsonArticleData article) =>
        ValidDetailValues(article.Symptoms, 200) && ValidDetailValues(article.Causes, 200) &&
        ValidDetailValues(article.Targets, 200) && ValidDetailValues(article.ErrorCodes, 100) &&
        ValidDetailValues(article.Procedures, 500) && ValidDetailValues(article.Cautions, 500) &&
        ValidDetailValues(article.SearchTerms, 200) && article.TagIds.Count <= 50 &&
        article.TagIds.All(id => Guid.TryParse(id, out _)) &&
        article.TagIds.Distinct(StringComparer.Ordinal).Count() == article.TagIds.Count;

    private static bool ValidDetailValues(IReadOnlyList<string>? values, int maximumRunes)
    {
        if (values is null || values.Count > 50)
        {
            return false;
        }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        return values.All(value => value is not null && ValidSingleLine(value.Trim(), maximumRunes) &&
            unique.Add(NormalizeSearchText(value)));
    }

    private Dictionary<string, string> ReadIdValueMap(string table, string column)
    {
        var safe = (table, column) switch
        {
            ("articles", "management_code") => ("articles", "management_code"),
            ("tags", "normalized_name") => ("tags", "normalized_name"),
            _ => throw new InvalidOperationException("JSON衝突検査の項目が正しくありません。")
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT id, {safe.Item2} FROM {safe.Item1}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static void AddJsonArticleParameters(
        SqliteCommand command,
        JsonArticleData article,
        string plainText,
        string actorUserId)
    {
        command.Parameters.AddWithValue("$id", article.Id);
        command.Parameters.AddWithValue("$category_id", article.CategoryId);
        command.Parameters.AddWithValue("$title", article.Title.Trim());
        command.Parameters.AddWithValue("$normalized_title", NormalizeSearchText(article.Title));
        command.Parameters.AddWithValue("$summary", article.Summary.Trim());
        command.Parameters.AddWithValue("$body_doc_json", article.BodyDoc.GetRawText());
        command.Parameters.AddWithValue("$body_plain_text", plainText);
        command.Parameters.AddWithValue("$status", article.Status);
        command.Parameters.AddWithValue("$importance", article.Importance);
        command.Parameters.AddWithValue("$new_badge_until", (object?)article.NewBadgeUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_badge_until", (object?)article.UpdatedBadgeUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_hidden", article.IsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", article.UpdatedAt);
        command.Parameters.AddWithValue("$deleted_at", (object?)article.DeletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", actorUserId);
    }

    private static bool TransferRowExists(SqliteTransaction transaction, string table, string id)
    {
        var safeTable = table == "articles" ? "articles" : throw new InvalidOperationException("JSON取込表が正しくありません。");
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {safeTable} WHERE id = $id)";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private void TemporarilyRenameImportedValues(
        SqliteTransaction transaction,
        string table,
        string column,
        IEnumerable<string> ids)
    {
        var safe = (table, column) switch
        {
            ("categories", "normalized_name") => ("categories", "normalized_name"),
            ("tags", "normalized_name") => ("tags", "normalized_name"),
            _ => throw new InvalidOperationException("JSON取込表が正しくありません。")
        };
        foreach (var id in ids)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {safe.Item1} SET {safe.Item2} = $temporary WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$temporary", $"__json_import_{id}");
            command.ExecuteNonQuery();
        }
    }

    private void ReplaceJsonArticleDetails(SqliteTransaction transaction, JsonArticleData article)
    {
        ReplaceJsonDetailTable(transaction, "article_symptoms", article.Id, article.Symptoms);
        ReplaceJsonDetailTable(transaction, "article_causes", article.Id, article.Causes);
        ReplaceJsonDetailTable(transaction, "article_targets", article.Id, article.Targets);
        ReplaceJsonDetailTable(transaction, "article_error_codes", article.Id, article.ErrorCodes);
        ReplaceJsonDetailTable(transaction, "article_search_terms", article.Id, article.SearchTerms);
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE articles SET procedure_text = $procedures, caution_text = $cautions WHERE id = $id";
            command.Parameters.AddWithValue("$id", article.Id);
            command.Parameters.AddWithValue("$procedures", string.Join('\n', article.Procedures));
            command.Parameters.AddWithValue("$cautions", string.Join('\n', article.Cautions));
            command.ExecuteNonQuery();
        }
        ExecuteTransferDelete(transaction, "DELETE FROM article_tags WHERE article_id = $id", article.Id);
        foreach (var tagId in article.TagIds)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO article_tags(article_id, tag_id) VALUES ($article_id, $tag_id)";
            insert.Parameters.AddWithValue("$article_id", article.Id);
            insert.Parameters.AddWithValue("$tag_id", tagId);
            insert.ExecuteNonQuery();
        }
    }

    private void ReplaceJsonDetailTable(
        SqliteTransaction transaction,
        string table,
        string articleId,
        IReadOnlyList<string> values)
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
        ExecuteTransferDelete(transaction, $"DELETE FROM {safeTable} WHERE article_id = $id", articleId);
        for (var index = 0; index < values.Count; index++)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {safeTable}(id, article_id, value, normalized_value, sort_order) VALUES ($id, $article_id, $value, $normalized_value, $sort_order)";
            insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
            insert.Parameters.AddWithValue("$article_id", articleId);
            insert.Parameters.AddWithValue("$value", values[index].Trim());
            insert.Parameters.AddWithValue("$normalized_value", NormalizeSearchText(values[index]));
            insert.Parameters.AddWithValue("$sort_order", index);
            insert.ExecuteNonQuery();
        }
    }

    private void UpsertJsonSearchDocument(SqliteTransaction transaction, JsonArticleData article, string body)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO article_search_documents(
                article_id, title, summary, body, symptoms, causes, targets,
                error_codes, tags, search_terms
            ) VALUES (
                $article_id, $title, $summary, $body, $symptoms, $causes, $targets,
                $error_codes, $tags, $search_terms
            )
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body,
                symptoms = excluded.symptoms, causes = excluded.causes, targets = excluded.targets,
                error_codes = excluded.error_codes, tags = excluded.tags,
                search_terms = excluded.search_terms
            """;
        command.Parameters.AddWithValue("$article_id", article.Id);
        command.Parameters.AddWithValue("$title", NormalizeSearchText(article.Title));
        command.Parameters.AddWithValue("$summary", NormalizeSearchText(article.Summary));
        command.Parameters.AddWithValue("$body", NormalizeSearchText(body));
        command.Parameters.AddWithValue("$symptoms", NormalizeValues(article.Symptoms));
        command.Parameters.AddWithValue("$causes", NormalizeValues(article.Causes));
        command.Parameters.AddWithValue("$targets", NormalizeValues(article.Targets));
        command.Parameters.AddWithValue("$error_codes", NormalizeValues(article.ErrorCodes));
        command.Parameters.AddWithValue("$tags", NormalizeValues(article.TagIds.Select(id => JsonTagName(transaction, id)).ToArray()));
        command.Parameters.AddWithValue("$search_terms", NormalizeValues(article.SearchTerms));
        command.ExecuteNonQuery();
    }

    private string JsonTagName(SqliteTransaction transaction, string id)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM tags WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    private static string NormalizeValues(IReadOnlyList<string> values) => string.Join(
        ' ', values.Select(NormalizeSearchText).Where(value => value.Length > 0));

    private void ExecuteTransferDelete(SqliteTransaction transaction, string sql, string id)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void UpdateManagementSequence(SqliteTransaction transaction, string entityType, string table)
    {
        var safeTable = table switch
        {
            "categories" => "categories",
            "articles" => "articles",
            _ => throw new InvalidOperationException("管理ID採番表が正しくありません。")
        };
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE management_code_sequences
               SET next_value = MAX(
                   next_value,
                   (SELECT COALESCE(MAX(CAST(SUBSTR(management_code, 5) AS INTEGER)), 0) + 1 FROM {safeTable})
               )
             WHERE entity_type = $entity_type
            """;
        command.Parameters.AddWithValue("$entity_type", entityType);
        command.ExecuteNonQuery();
    }

    private static AppProblemException JsonReadProblem(string message) => new(new AppProblem(
        "JSON-002",
        message,
        "KnowledgeAppの「.knowledge-export.json」ファイルを選び直してください。"));

    private static AppProblemException JsonWriteProblem() => new(new AppProblem(
        "JSON-005",
        "JSONファイルを書き出せませんでした。",
        "保存先の空き容量と書き込み権限を確認し、もう一度お試しください。"));

    private static AppProblemException JsonImportProblem(string message) => new(new AppProblem(
        "JSON-004",
        message,
        "KnowledgeAppから画像を除外して再度書き出してください。"));

    private sealed record JsonArticleBaseRow(
        string Id,
        string ManagementCode,
        string CategoryId,
        string Title,
        string Summary,
        string BodyDocumentJson,
        string Status,
        long Importance,
        string? NewBadgeUntil,
        string? UpdatedBadgeUntil,
        bool IsHidden,
        string CreatedAt,
        string UpdatedAt,
        string? DeletedAt,
        string ProcedureText,
        string CautionText);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const string CsvFormatVersion = "2";
    private const int MaximumCsvBytes = 50 * 1024 * 1024;
    private static readonly string[] CsvHeaders =
    [
        "形式バージョン", "FAQ管理ID", "分類管理ID", "分類パス", "タイトル", "概要",
        "回答本文", "回答本文ハッシュ", "状態", "重要度", "新着表示終了日", "更新表示終了日",
        "非表示", "作成者", "更新者", "作成日時", "更新日時"
    ];

    internal CsvExportResult ExportFaqCsv(string destination) => ExecuteLocked(() =>
    {
        var records = new List<IReadOnlyList<string>> { CsvHeaders };
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE category_paths(id, management_code, path) AS (
                SELECT id, management_code, name FROM categories WHERE parent_id IS NULL
                UNION ALL
                SELECT child.id, child.management_code, parent.path || ' > ' || child.name
                  FROM categories child
                  JOIN category_paths parent ON child.parent_id = parent.id
            )
            SELECT article.management_code, category_paths.management_code, category_paths.path,
                   article.title, article.summary, article.body_plain_text, article.status,
                   article.importance, article.new_badge_until, article.updated_badge_until,
                   article.is_hidden, creator.display_name, updater.display_name,
                   article.created_at, article.updated_at
              FROM articles article
              JOIN category_paths ON category_paths.id = article.category_id
              JOIN users creator ON creator.id = article.created_by_user_id
              JOIN users updater ON updater.id = article.updated_by_user_id
             WHERE article.deleted_at IS NULL
             ORDER BY category_paths.path, article.title, article.management_code
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var body = reader.GetString(5);
            records.Add([
                CsvFormatVersion,
                reader.GetString(0),
                reader.GetString(1),
                SafeExcelCell(reader.GetString(2)),
                SafeExcelCell(reader.GetString(3)),
                SafeExcelCell(reader.GetString(4)),
                SafeExcelCell(body),
                BodyHash(body),
                CsvStatusLabel(reader.GetString(6)),
                reader.GetInt64(7).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                reader.GetInt64(10) == 1 ? "TRUE" : "FALSE",
                SafeExcelCell(reader.GetString(11)),
                SafeExcelCell(reader.GetString(12)),
                reader.GetString(13),
                reader.GetString(14)
            ]);
        }
        try
        {
            PublishTransferFile(destination, Rfc4180Csv.Write(records), "CSV");
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CsvWriteProblem();
        }
        return new CsvExportResult(destination, records.Count - 1);
    });

    internal CsvImportPreview InspectFaqCsv(string source) => ExecuteLocked(() =>
    {
        var (fileSha256, plans) = PlanFaqCsv(source);
        return CsvPreview(source, fileSha256, plans);
    });

    internal CsvImportResult ImportFaqCsv(
        string source,
        string expectedFileSha256,
        string actorUserId,
        string safetyBackupPath) => ExecuteLocked(() =>
    {
        var (fileSha256, plans) = PlanFaqCsv(source);
        if (!string.Equals(fileSha256, expectedFileSha256, StringComparison.Ordinal))
        {
            throw new AppProblemException(new AppProblem(
                "CSV-007",
                "確認後にCSVファイルが変更されています。",
                "CSVをもう一度プレビューしてから取り込んでください。"));
        }
        if (plans.Any(plan => plan.Action == CsvRowAction.Error))
        {
            throw new AppProblemException(new AppProblem(
                "CSV-004",
                "エラーがあるためCSVを取り込めません。",
                "プレビューに表示された行を修正し、もう一度選択してください。"));
        }

        using var transaction = _connection.BeginTransaction();
        var now = UtcNow();
        long created = 0;
        long updated = 0;
        long unchanged = 0;
        foreach (var plan in plans)
        {
            if (plan.Action == CsvRowAction.Unchanged)
            {
                unchanged++;
                continue;
            }
            if (plan.Action == CsvRowAction.Create)
            {
                created++;
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO articles(
                        id, category_id, title, normalized_title, summary, body_doc_json,
                        body_format_version, body_plain_text, status, importance, created_at,
                        updated_at, new_badge_until, updated_badge_until, is_hidden,
                        created_by_user_id, updated_by_user_id
                    ) VALUES (
                        $id, $category_id, $title, $normalized_title, $summary, $body_doc_json,
                        2, $body_plain_text, $status, $importance, $now, $now,
                        $new_badge_until, $updated_badge_until, $is_hidden, $actor, $actor
                    )
                    """;
                AddCsvArticleParameters(insert, plan, actorUserId, now);
                insert.ExecuteNonQuery();
            }
            else
            {
                updated++;
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE articles
                       SET category_id = $category_id, title = $title,
                           normalized_title = $normalized_title, summary = $summary,
                           body_doc_json = $body_doc_json, body_format_version = 2,
                           body_plain_text = $body_plain_text, status = $status,
                           importance = $importance, new_badge_until = $new_badge_until,
                           updated_badge_until = $updated_badge_until, is_hidden = $is_hidden,
                           updated_at = $now, updated_by_user_id = $actor
                     WHERE id = $id AND deleted_at IS NULL
                    """;
                AddCsvArticleParameters(update, plan, actorUserId, now);
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new AppProblemException(AppProblem.Database("CSVの対象FAQを更新できませんでした。"));
                }
            }
            UpsertTransferSearchDocument(transaction, plan.ArticleId, plan.Title, plan.Summary, plan.BodyPlainText);
            RebuildArticleFts(transaction, plan.ArticleId);
        }
        transaction.Commit();
        return new CsvImportResult(source, safetyBackupPath, created, updated, unchanged);
    });

    private (string Hash, IReadOnlyList<CsvImportPlanRow> Plans) PlanFaqCsv(string source)
    {
        byte[] bytes;
        try
        {
            bytes = FileSystemBoundary.ReadBoundedFile(source, MaximumCsvBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CsvReadProblem("CSVファイルを読み込めませんでした。");
        }
        if (bytes.Length > MaximumCsvBytes)
        {
            throw CsvReadProblem("CSVファイルが50MBを超えています。");
        }
        var fileSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var records = Rfc4180Csv.Read(bytes);
        if (records.Count == 0 || !records[0].SequenceEqual(CsvHeaders, StringComparer.Ordinal))
        {
            throw new AppProblemException(new AppProblem(
                "CSV-002",
                "CSVの見出しがKnowledgeApp形式と一致しません。",
                "KnowledgeAppから書き出したCSVを使用し、列名や列順は変更しないでください。"));
        }

        var categories = CategoryCsvMaps();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<CsvImportPlanRow>();
        for (var index = 1; index < records.Count; index++)
        {
            var line = index + 1L;
            var record = records[index];
            plans.Add(record.Count == CsvHeaders.Length
                ? PlanCsvRecord(line, record, categories, seenIds)
                : CsvErrorPlan(line, "CSVの列数または引用符が正しくありません。"));
        }
        return (fileSha256, plans);
    }

    private (IReadOnlyDictionary<string, string> ByManagementId, IReadOnlyDictionary<string, string> ByPath)
        CategoryCsvMaps()
    {
        var byManagementId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE paths(id, management_code, path) AS (
                SELECT id, management_code, name FROM categories WHERE parent_id IS NULL
                UNION ALL
                SELECT child.id, child.management_code, parent.path || ' > ' || child.name
                  FROM categories child JOIN paths parent ON child.parent_id = parent.id
            )
            SELECT id, management_code, path FROM paths
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            byManagementId[reader.GetString(1)] = reader.GetString(0);
            byPath[reader.GetString(2)] = reader.GetString(0);
        }
        return (byManagementId, byPath);
    }

    private CsvImportPlanRow PlanCsvRecord(
        long line,
        IReadOnlyList<string> record,
        (IReadOnlyDictionary<string, string> ByManagementId, IReadOnlyDictionary<string, string> ByPath) categories,
        HashSet<string> seenIds)
    {
        if (record[0] != CsvFormatVersion)
        {
            return CsvErrorPlan(line, "対応していない形式バージョンです。");
        }
        var managementId = record[1].Trim().ToUpperInvariant();
        if (managementId.Length > 0 && !seenIds.Add(managementId))
        {
            return CsvErrorPlan(line, "同じFAQ管理IDがCSV内に複数あります。");
        }
        var categoryManagementId = record[2].Trim().ToUpperInvariant();
        var categoryPath = UnsafeExcelCell(record[3]).Trim();
        var categoryFound = categoryPath.Length > 0
            ? categories.ByPath.TryGetValue(categoryPath, out var categoryId)
            : categories.ByManagementId.TryGetValue(categoryManagementId, out categoryId);
        if (!categoryFound || string.IsNullOrWhiteSpace(categoryId))
        {
            return CsvErrorPlan(line, "分類管理IDまたは分類パスが現在の分類一覧にありません。");
        }

        var title = UnsafeExcelCell(record[4]).Trim();
        var summary = UnsafeExcelCell(record[5]).Trim();
        var body = NormalizeNewlines(UnsafeExcelCell(record[6]));
        if (title.EnumerateRunes().Count() is < 1 or > 200)
        {
            return CsvErrorPlan(line, "タイトルは1～200文字で入力してください。");
        }
        if (summary.EnumerateRunes().Count() > 500)
        {
            return CsvErrorPlan(line, "概要は500文字以内で入力してください。");
        }
        var status = record[8].Trim() switch
        {
            "下書き" or "draft" => ArticleStatuses.Draft,
            "公開" or "published" => ArticleStatuses.Published,
            "廃止" or "archived" => ArticleStatuses.Archived,
            _ => string.Empty
        };
        if (status.Length == 0)
        {
            return CsvErrorPlan(line, "状態は「下書き」「公開」「廃止」のいずれかにしてください。");
        }
        if (!long.TryParse(record[9].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var importance) ||
            importance is < 1 or > 3)
        {
            return CsvErrorPlan(line, "重要度は1～3で入力してください。");
        }
        if (!TryCsvDate(record[10], out var newBadgeUntil) || !TryCsvDate(record[11], out var updatedBadgeUntil))
        {
            return CsvErrorPlan(line, "日付はYYYY-MM-DD形式で入力してください。");
        }
        var hiddenText = record[12].Trim().ToUpperInvariant();
        bool isHidden;
        if (hiddenText is "TRUE" or "1" or "はい")
        {
            isHidden = true;
        }
        else if (hiddenText is "FALSE" or "0" or "いいえ" or "")
        {
            isHidden = false;
        }
        else
        {
            return CsvErrorPlan(line, "非表示はTRUEまたはFALSEで入力してください。");
        }

        var existing = managementId.Length == 0 ? null : ExistingCsvArticleByManagementCode(managementId);
        if (managementId.Length > 0 && existing is null)
        {
            return CsvErrorPlan(line, "FAQ管理IDに一致する登録中のFAQがありません。");
        }
        var bodyWillBeReplaced = existing is null || !string.Equals(
            BodyHash(body), record[7].Trim(), StringComparison.OrdinalIgnoreCase);
        if (status == ArticleStatuses.Published && bodyWillBeReplaced && string.IsNullOrWhiteSpace(body))
        {
            return CsvErrorPlan(line, "公開FAQの回答本文は空欄にできません。");
        }
        if (existing is not null)
        {
            if (status == ArticleStatuses.Published && !bodyWillBeReplaced &&
                string.IsNullOrWhiteSpace(existing.BodyPlainText) && !existing.HasAttachments)
            {
                return CsvErrorPlan(line, "公開FAQの回答本文は空欄にできません。");
            }
            if (existing.IsMergeTarget && (status != ArticleStatuses.Published || isHidden))
            {
                return CsvErrorPlan(line, "統合先FAQは公開かつ非表示OFFを維持してください。");
            }
        }

        string articleId;
        string bodyDocumentJson;
        string effectiveBody;
        CsvRowAction action;
        bool stale;
        if (existing is null)
        {
            articleId = Guid.CreateVersion7().ToString();
            bodyDocumentJson = PlainTextDocumentJson(body);
            effectiveBody = body;
            action = CsvRowAction.Create;
            stale = false;
        }
        else
        {
            articleId = existing.ArticleId;
            bodyDocumentJson = bodyWillBeReplaced ? PlainTextDocumentJson(body) : existing.BodyDocumentJson;
            effectiveBody = bodyWillBeReplaced ? body : existing.BodyPlainText;
            var changed = categoryId != existing.CategoryId || title != existing.Title ||
                summary != existing.Summary || effectiveBody != existing.BodyPlainText ||
                status != existing.Status || importance != existing.Importance ||
                newBadgeUntil != existing.NewBadgeUntil || updatedBadgeUntil != existing.UpdatedBadgeUntil ||
                isHidden != existing.IsHidden;
            action = changed ? CsvRowAction.Update : CsvRowAction.Unchanged;
            stale = changed && record[16].Trim() != existing.UpdatedAt;
        }
        var messages = new List<string>();
        if (bodyWillBeReplaced)
        {
            messages.Add("回答本文をプレーンテキスト段落へ置換します（表・画像・書式は本文から外れます）。");
        }
        if (stale)
        {
            messages.Add("書き出し後に更新されていますが、指定どおりCSVの内容で上書きします。");
        }
        return new CsvImportPlanRow(
            line, action, managementId.Length == 0 ? null : managementId, articleId, categoryId,
            title, summary, effectiveBody, bodyDocumentJson, bodyWillBeReplaced, status, importance,
            newBadgeUntil, updatedBadgeUntil, isHidden, stale, messages);
    }

    private ExistingCsvArticle? ExistingCsvArticleByManagementCode(string managementCode)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, category_id, title, summary, body_plain_text, body_doc_json, status,
                   importance, new_badge_until, updated_badge_until, is_hidden, updated_at,
                   EXISTS(SELECT 1 FROM article_attachments WHERE article_id = articles.id),
                   EXISTS(SELECT 1 FROM article_merge_relations WHERE target_article_id = articles.id)
              FROM articles
             WHERE management_code = $management_code COLLATE NOCASE AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$management_code", managementCode);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new ExistingCsvArticle(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10) == 1,
            reader.GetString(11), reader.GetInt64(12) == 1, reader.GetInt64(13) == 1);
    }

    private static void AddCsvArticleParameters(
        SqliteCommand command,
        CsvImportPlanRow plan,
        string actorUserId,
        string now)
    {
        command.Parameters.AddWithValue("$id", plan.ArticleId);
        command.Parameters.AddWithValue("$category_id", plan.CategoryId);
        command.Parameters.AddWithValue("$title", plan.Title);
        command.Parameters.AddWithValue("$normalized_title", NormalizeSearchText(plan.Title));
        command.Parameters.AddWithValue("$summary", plan.Summary);
        command.Parameters.AddWithValue("$body_doc_json", plan.BodyDocumentJson);
        command.Parameters.AddWithValue("$body_plain_text", plan.BodyPlainText);
        command.Parameters.AddWithValue("$status", plan.Status);
        command.Parameters.AddWithValue("$importance", plan.Importance);
        command.Parameters.AddWithValue("$new_badge_until", (object?)plan.NewBadgeUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_badge_until", (object?)plan.UpdatedBadgeUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_hidden", plan.IsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$actor", actorUserId);
    }

    private void UpsertTransferSearchDocument(
        SqliteTransaction transaction,
        string articleId,
        string title,
        string summary,
        string body)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO article_search_documents(article_id, title, summary, body)
            VALUES ($article_id, $title, $summary, $body)
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body
            """;
        command.Parameters.AddWithValue("$article_id", articleId);
        command.Parameters.AddWithValue("$title", NormalizeSearchText(title));
        command.Parameters.AddWithValue("$summary", NormalizeSearchText(summary));
        command.Parameters.AddWithValue("$body", NormalizeSearchText(body));
        command.ExecuteNonQuery();
    }

    private static CsvImportPreview CsvPreview(
        string source,
        string fileSha256,
        IReadOnlyList<CsvImportPlanRow> plans) => new(
            source,
            fileSha256,
            plans.Count,
            plans.LongCount(plan => plan.Action == CsvRowAction.Create),
            plans.LongCount(plan => plan.Action == CsvRowAction.Update),
            plans.LongCount(plan => plan.Action == CsvRowAction.Unchanged),
            plans.LongCount(plan => plan.BodyWillBeReplaced),
            plans.LongCount(plan => plan.StaleUpdateWillOverwrite),
            plans.LongCount(plan => plan.Action == CsvRowAction.Error),
            plans.Select(plan => new CsvImportPreviewRow(
                plan.Line,
                plan.Action switch
                {
                    CsvRowAction.Create => "create",
                    CsvRowAction.Update => "update",
                    CsvRowAction.Unchanged => "unchanged",
                    _ => "error"
                },
                plan.FaqManagementId,
                plan.Title,
                plan.BodyWillBeReplaced,
                plan.StaleUpdateWillOverwrite,
                plan.Messages)).ToArray());

    private static CsvImportPlanRow CsvErrorPlan(long line, string message) => new(
        line, CsvRowAction.Error, null, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, false, ArticleStatuses.Draft, 1, null, null, false, false, [message]);

    private static bool TryCsvDate(string value, out string? result)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            result = null;
            return true;
        }
        result = trimmed;
        return DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static string BodyHash(string plainText) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeNewlines(plainText))));

    private static string NormalizeNewlines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static string PlainTextDocumentJson(string plainText)
    {
        var content = NormalizeNewlines(plainText).Split('\n').Select(line => line.Length == 0
            ? new Dictionary<string, object?> { ["type"] = "paragraph" }
            : new Dictionary<string, object?>
            {
                ["type"] = "paragraph",
                ["content"] = new[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = line }
                }
            }).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "doc",
            ["content"] = content
        });
    }

    private static string SafeExcelCell(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? $"'{value}" : value;

    private static string UnsafeExcelCell(string value) =>
        value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;

    private static string CsvStatusLabel(string status) => status switch
    {
        ArticleStatuses.Published => "公開",
        ArticleStatuses.Archived => "廃止",
        _ => "下書き"
    };

    private static void PublishTransferFile(string destination, byte[] content, string label)
    {
        FileSystemBoundary.ValidatePath(destination);
        var temporary = $"{destination}.{Guid.CreateVersion7():D}.partial";
        var previous = $"{destination}.{Guid.CreateVersion7():D}.previous.partial";
        var hadPrevious = File.Exists(destination);
        try
        {
            FileSystemBoundary.ValidatePath(temporary);
            FileSystemBoundary.ValidatePath(previous);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (hadPrevious)
            {
                FileSystemBoundary.ValidatePath(destination);
                File.Move(destination, previous);
            }
            try
            {
                FileSystemBoundary.ValidatePath(temporary);
                FileSystemBoundary.ValidatePath(destination);
                File.Move(temporary, destination);
            }
            catch
            {
                if (hadPrevious && File.Exists(previous) && !File.Exists(destination))
                {
                    File.Move(previous, destination);
                }
                throw;
            }
            if (hadPrevious && File.Exists(previous))
            {
                File.Delete(FileSystemBoundary.ValidatePath(previous));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeletePartial(temporary);
            if (label == "CSV")
            {
                throw CsvWriteProblem();
            }
            throw JsonWriteProblem();
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(FileSystemBoundary.ValidatePath(path));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 同一フォルダ内のUUID付き生成途中ファイルだけなので次回の手動整理に任せる。
        }
    }

    private static AppProblemException CsvWriteProblem() => new(new AppProblem(
        "CSV-001",
        "CSVファイルを書き出せませんでした。",
        "保存先の空き容量と書込み権限を確認し、Excelで開いている場合は閉じてください。"));

    private static AppProblemException CsvReadProblem(string message) => new(new AppProblem(
        "CSV-003",
        message,
        "KnowledgeAppから書き出したUTF-8のCSVを選択してください。"));
}

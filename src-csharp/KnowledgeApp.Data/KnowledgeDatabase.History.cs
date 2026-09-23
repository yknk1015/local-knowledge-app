using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const long HistoryPageSize = 50;

    internal SearchLogPage ListSearchLogs(ListSearchLogsInput input, string? userId = null) => ExecuteLocked(() =>
    {
        var (startAt, endBefore) = HistoryDateBounds(input.StartDate, input.EndDate);
        var normalizedQuery = NormalizeSearchText((input.Query ?? string.Empty).Trim());
        var likeQuery = $"%{EscapeLikeForHistory(normalizedQuery)}%";
        using var count = _connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*)
              FROM search_logs
             WHERE ($history_user IS NULL OR COALESCE(user_id, '00000000-0000-7000-8000-000000000000') = $history_user)
               AND ($normalized_query = '' OR normalized_query LIKE $like_query ESCAPE '\')
               AND ($start_at IS NULL OR created_at >= $start_at)
               AND ($end_before IS NULL OR created_at < $end_before)
               AND ($zero_results_only = 0 OR result_count = 0)
            """;
        AddHistoryFilterParameters(
            count, userId, normalizedQuery, likeQuery, startAt, endBefore, input.ZeroResultsOnly);
        var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        var page = ClampPage(input.Page, total);
        var offset = (page - 1) * HistoryPageSize;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT search_log.id, search_log.query_text, search_log.normalized_query,
                   search_log.scope, category.name, search_log.result_count, search_log.created_at
              FROM search_logs search_log
              LEFT JOIN categories category ON category.id = search_log.category_id
             WHERE ($history_user IS NULL OR COALESCE(search_log.user_id, '00000000-0000-7000-8000-000000000000') = $history_user)
               AND ($normalized_query = '' OR search_log.normalized_query LIKE $like_query ESCAPE '\')
               AND ($start_at IS NULL OR search_log.created_at >= $start_at)
               AND ($end_before IS NULL OR search_log.created_at < $end_before)
               AND ($zero_results_only = 0 OR search_log.result_count = 0)
             ORDER BY search_log.created_at DESC, search_log.id DESC
             LIMIT $limit OFFSET $offset
            """;
        AddHistoryFilterParameters(
            command, userId, normalizedQuery, likeQuery, startAt, endBefore, input.ZeroResultsOnly);
        command.Parameters.AddWithValue("$limit", HistoryPageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<SearchLogItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SearchLogItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetString(6)));
        }
        return new SearchLogPage(items, total, page, HistoryPageSize);
    });

    internal ViewLogPage ListViewLogs(ListViewLogsInput input, string? userId = null, bool publicOnly = false) => ExecuteLocked(() =>
    {
        var (startAt, endBefore) = HistoryDateBounds(input.StartDate, input.EndDate);
        var normalizedQuery = NormalizeSearchText((input.Query ?? string.Empty).Trim());
        var likeQuery = $"%{EscapeLikeForHistory(normalizedQuery)}%";
        using var count = _connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*)
              FROM view_logs view_log
              JOIN articles article ON article.id = view_log.article_id
              LEFT JOIN search_logs search_log ON search_log.id = view_log.source_search_log_id
             WHERE ($public_only = 0 OR (article.status = 'published' AND article.is_hidden = 0 AND article.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM article_merge_relations r WHERE r.source_article_id = article.id)))
               AND ($history_user IS NULL OR COALESCE(view_log.user_id, '00000000-0000-7000-8000-000000000000') = $history_user)
               AND ($normalized_query = '' OR article.normalized_title LIKE $like_query ESCAPE '\'
                    OR search_log.normalized_query LIKE $like_query ESCAPE '\')
               AND ($start_at IS NULL OR view_log.viewed_at >= $start_at)
               AND ($end_before IS NULL OR view_log.viewed_at < $end_before)
            """;
        AddHistoryFilterParameters(count, userId, normalizedQuery, likeQuery, startAt, endBefore, null);
        count.Parameters.AddWithValue("$public_only", publicOnly ? 1 : 0);
        var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        var page = ClampPage(input.Page, total);
        var offset = (page - 1) * HistoryPageSize;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT view_log.id, article.id, article.title, search_log.query_text, view_log.viewed_at
              FROM view_logs view_log
              JOIN articles article ON article.id = view_log.article_id
              LEFT JOIN search_logs search_log ON search_log.id = view_log.source_search_log_id
             WHERE ($public_only = 0 OR (article.status = 'published' AND article.is_hidden = 0 AND article.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM article_merge_relations r WHERE r.source_article_id = article.id)))
               AND ($history_user IS NULL OR COALESCE(view_log.user_id, '00000000-0000-7000-8000-000000000000') = $history_user)
               AND ($normalized_query = '' OR article.normalized_title LIKE $like_query ESCAPE '\'
                    OR search_log.normalized_query LIKE $like_query ESCAPE '\')
               AND ($start_at IS NULL OR view_log.viewed_at >= $start_at)
               AND ($end_before IS NULL OR view_log.viewed_at < $end_before)
             ORDER BY view_log.viewed_at DESC, view_log.id DESC
             LIMIT $limit OFFSET $offset
            """;
        AddHistoryFilterParameters(command, userId, normalizedQuery, likeQuery, startAt, endBefore, null);
        command.Parameters.AddWithValue("$limit", HistoryPageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<ViewLogItem>();
        command.Parameters.AddWithValue("$public_only", publicOnly ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new ViewLogItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        }
        return new ViewLogPage(items, total, page, HistoryPageSize);
    });

    internal long DeleteHistory(DeleteHistoryInput input, string? userId = null) => ExecuteLocked(() =>
    {
        if (!input.DeleteAll && string.IsNullOrWhiteSpace(input.StartDate) && string.IsNullOrWhiteSpace(input.EndDate))
        {
            throw HistoryService.HistoryInputProblem(
                "履歴を削除する期間が指定されていません。",
                "開始日または終了日を指定するか、全履歴削除を選んでください。");
        }
        var (startAt, endBefore) = HistoryDateBounds(input.StartDate, input.EndDate);
        var (table, timestamp) = input.Target switch
        {
            HistoryTargets.Search => ("search_logs", "created_at"),
            HistoryTargets.View => ("view_logs", "viewed_at"),
            _ => throw HistoryService.HistoryInputProblem(
                "削除する履歴の種類が正しくありません。",
                "検索履歴または閲覧履歴を選び直してください。")
        };
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = input.DeleteAll
            ? $"DELETE FROM {table} WHERE ($history_user IS NULL OR COALESCE(user_id, '00000000-0000-7000-8000-000000000000') = $history_user)"
            : $"DELETE FROM {table} WHERE ($history_user IS NULL OR COALESCE(user_id, '00000000-0000-7000-8000-000000000000') = $history_user) AND ($start_at IS NULL OR {timestamp} >= $start_at) AND ($end_before IS NULL OR {timestamp} < $end_before)";
        if (!input.DeleteAll)
        {
            command.Parameters.AddWithValue("$start_at", (object?)startAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$end_before", (object?)endBefore ?? DBNull.Value);
        }
        command.Parameters.AddWithValue("$history_user", (object?)userId ?? DBNull.Value);
        var deleted = command.ExecuteNonQuery();
        transaction.Commit();
        return deleted;
    });

    internal SyntheticHistoryFixture SeedSyntheticHistoryFixture(string articleId, string categoryId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var focusSearchId = InsertSyntheticSearchLog(
            transaction, "ＰＣ 設定", SearchScopes.Current, categoryId, 2, "2026-08-29T10:00:00+00:00");
        var zeroSearchId = InsertSyntheticSearchLog(
            transaction, "Zoom 失敗", SearchScopes.All, null, 0, "2026-08-30T10:00:00+00:00");
        var laterSearchId = InsertSyntheticSearchLog(
            transaction, "Zoom 成功", SearchScopes.Descendants, categoryId, 3, "2026-08-31T10:00:00+00:00");
        for (var index = 0; index < 51; index++)
        {
            InsertSyntheticSearchLog(
                transaction,
                $"ページング確認 {index + 1:00}",
                SearchScopes.All,
                null,
                index + 1,
                $"2026-08-01T00:{index:00}:00+00:00");
        }
        var viewFromSearchId = InsertSyntheticViewLog(
            transaction, articleId, zeroSearchId, "2026-08-30T11:00:00+00:00");
        var directViewId = InsertSyntheticViewLog(
            transaction, articleId, null, "2026-08-31T11:00:00+00:00");
        transaction.Commit();
        return new SyntheticHistoryFixture(
            focusSearchId, zeroSearchId, laterSearchId, viewFromSearchId, directViewId);
    });

    private static void AddHistoryFilterParameters(
        SqliteCommand command,
        string? userId,
        string normalizedQuery,
        string likeQuery,
        string? startAt,
        string? endBefore,
        bool? zeroResultsOnly)
    {
        command.Parameters.AddWithValue("$history_user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalized_query", normalizedQuery);
        command.Parameters.AddWithValue("$like_query", likeQuery);
        command.Parameters.AddWithValue("$start_at", (object?)startAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$end_before", (object?)endBefore ?? DBNull.Value);
        if (zeroResultsOnly is not null)
        {
            command.Parameters.AddWithValue("$zero_results_only", zeroResultsOnly.Value ? 1 : 0);
        }
    }

    private static long ClampPage(long requestedPage, long total)
    {
        var totalPages = Math.Max(1, (total + HistoryPageSize - 1) / HistoryPageSize);
        return Math.Clamp(requestedPage, 1, totalPages);
    }

    private static (string? StartAt, string? EndBefore) HistoryDateBounds(
        string? startDate,
        string? endDate)
    {
        var start = ParseHistoryDate(startDate, "開始日");
        var end = ParseHistoryDate(endDate, "終了日");
        if (start is not null && end is not null && start > end)
        {
            throw HistoryService.HistoryInputProblem(
                "開始日は終了日以前にしてください。",
                "履歴の期間を確認して、もう一度お試しください。");
        }
        string? endBefore = null;
        if (end is not null)
        {
            try
            {
                endBefore = HistoryTimestamp(end.Value.AddDays(1));
            }
            catch (ArgumentOutOfRangeException)
            {
                throw HistoryService.HistoryInputProblem(
                    "終了日を処理できませんでした。",
                    "終了日を確認して、もう一度お試しください。");
            }
        }
        return (start is null ? null : HistoryTimestamp(start.Value), endBefore);
    }

    private static DateOnly? ParseHistoryDate(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!DateOnly.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw HistoryService.HistoryInputProblem(
                $"{label}の日付形式が正しくありません。",
                "日付を年-月-日の形式で指定してください。");
        }
        return parsed;
    }

    private static string HistoryTimestamp(DateOnly date) =>
        $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00+00:00";

    private static string EscapeLikeForHistory(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private string InsertSyntheticSearchLog(
        SqliteTransaction transaction,
        string query,
        string scope,
        string? categoryId,
        long resultCount,
        string createdAt)
    {
        var id = Guid.CreateVersion7().ToString();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_logs(
                id, query_text, normalized_query, scope, category_id, result_count, created_at
            ) VALUES (
                $id, $query_text, $normalized_query, $scope, $category_id, $result_count, $created_at
            )
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$query_text", query);
        command.Parameters.AddWithValue("$normalized_query", NormalizeSearchText(query));
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$category_id", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$result_count", resultCount);
        command.Parameters.AddWithValue("$created_at", createdAt);
        command.ExecuteNonQuery();
        return id;
    }

    private string InsertSyntheticViewLog(
        SqliteTransaction transaction,
        string articleId,
        string? sourceSearchLogId,
        string viewedAt)
    {
        var id = Guid.CreateVersion7().ToString();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO view_logs(id, article_id, source_search_log_id, viewed_at)
            VALUES ($id, $article_id, $source_search_log_id, $viewed_at)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$article_id", articleId);
        command.Parameters.AddWithValue("$source_search_log_id", (object?)sourceSearchLogId ?? DBNull.Value);
        command.Parameters.AddWithValue("$viewed_at", viewedAt);
        command.ExecuteNonQuery();
        return id;
    }
}

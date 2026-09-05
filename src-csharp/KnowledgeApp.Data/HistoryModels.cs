namespace KnowledgeApp.Data;

public static class HistoryTargets
{
    public const string Search = "search";
    public const string View = "view";

    public static bool IsValid(string value) => value is Search or View;
}

public sealed record ListSearchLogsInput(
    string Query,
    string? StartDate,
    string? EndDate,
    bool ZeroResultsOnly,
    long Page);

public sealed record SearchLogItem(
    string Id,
    string QueryText,
    string NormalizedQuery,
    string Scope,
    string? CategoryName,
    long ResultCount,
    string CreatedAt);

public sealed record SearchLogPage(
    IReadOnlyList<SearchLogItem> Items,
    long Total,
    long Page,
    long PageSize);

public sealed record ListViewLogsInput(
    string Query,
    string? StartDate,
    string? EndDate,
    long Page);

public sealed record ViewLogItem(
    string Id,
    string ArticleId,
    string ArticleTitle,
    string? SourceQueryText,
    string ViewedAt);

public sealed record ViewLogPage(
    IReadOnlyList<ViewLogItem> Items,
    long Total,
    long Page,
    long PageSize);

public sealed record DeleteHistoryInput(
    string Target,
    string? StartDate,
    string? EndDate,
    bool DeleteAll);

internal sealed record SyntheticHistoryFixture(
    string FocusSearchId,
    string ZeroSearchId,
    string LaterSearchId,
    string ViewFromSearchId,
    string DirectViewId);

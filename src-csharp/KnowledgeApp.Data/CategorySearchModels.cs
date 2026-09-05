namespace KnowledgeApp.Data;

public static class SearchScopes
{
    public const string Descendants = "descendants";
    public const string Current = "current";
    public const string All = "all";

    public static bool IsValid(string value) => value is Descendants or Current or All;
}

public static class SearchSorts
{
    public const string UpdatedDesc = "updatedDesc";
    public const string UpdatedAsc = "updatedAsc";
    public const string ImportanceDesc = "importanceDesc";
    public const string ImportanceAsc = "importanceAsc";

    public static bool IsValid(string value) =>
        value is UpdatedDesc or UpdatedAsc or ImportanceDesc or ImportanceAsc;
}

public sealed record CategorySummary(
    string Id,
    string? ParentId,
    string Name,
    string Description,
    long Depth,
    long SortOrder,
    long ArticleCount);

public sealed record SearchArticlesInput(
    string Query,
    string? CategoryId,
    string Scope,
    bool IncludeDrafts,
    long Page,
    string Sort);

public sealed record SearchArticleListItem(
    string Id,
    string CategoryId,
    string CategoryName,
    string Title,
    string Summary,
    string Status,
    long Importance,
    string? NewBadgeUntil,
    string? UpdatedBadgeUntil,
    bool IsHidden,
    string UpdatedAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> MatchReasons);

public sealed record SearchArticlePage(
    IReadOnlyList<SearchArticleListItem> Items,
    long Total,
    long Page,
    long PageSize);

public sealed record SynonymGroupSummary(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Terms,
    string UpdatedAt);

public sealed record SyntheticCategorySearchFixture(
    string NetworkCategoryId,
    string WifiCategoryId,
    string SecurityCategoryId,
    string PublishedMeshArticleId,
    string RelatedArticleId,
    string DraftArticleId,
    string HiddenArticleId,
    string DeletedArticleId,
    string MergedArticleId);

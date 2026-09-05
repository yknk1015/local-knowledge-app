using System.Text.Json;

namespace KnowledgeApp.Data;

public static class ArticleStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    public static bool IsValid(string value) => value is Draft or Published or Archived;
}

public sealed record SaveArticleInput(
    string? Id,
    string CategoryId,
    string Title,
    string Summary,
    JsonElement BodyDoc,
    string Status,
    long Importance,
    string? NewBadgeUntil,
    string? UpdatedBadgeUntil,
    bool IsHidden,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> Procedures,
    IReadOnlyList<string> Cautions,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> RelatedArticleIds);

public sealed record TagMasterItem(
    string Id,
    string Name,
    long UsageCount,
    string UpdatedAt);

public sealed record ManagementArticlesInput(
    string Query,
    string? CategoryId,
    string? Status,
    bool Deleted,
    long Page);

public sealed record ManagementArticleListItem(
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
    string CreatedByDisplayName,
    string UpdatedByDisplayName,
    string? DeletedAt,
    ArticleMergeInfo? MergeInfo);

public sealed record ManagementArticlePage(
    IReadOnlyList<ManagementArticleListItem> Items,
    long Total,
    long Page,
    long PageSize);

public sealed record StagedArticleImage(
    string Id,
    string OriginalName,
    string MediaType,
    long ByteSize,
    string Sha256,
    string AltText,
    string AssetPath);

public sealed record StageArticleImageBase64Input(
    string OriginalName,
    string BytesBase64);

internal sealed record AttachmentReference(
    string Id,
    string AltText);

internal sealed record AttachmentRecord(
    string Id,
    string RelativePath,
    string OriginalName,
    string MediaType,
    long ByteSize,
    string Sha256,
    string AltText,
    string CreatedAt);

internal sealed record ValidatedRichContent(
    string PlainText,
    IReadOnlyList<AttachmentReference> Attachments);

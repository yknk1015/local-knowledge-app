using System.Text.Json;

namespace KnowledgeApp.Data;

public sealed record ArticleDetail(
    string Id,
    string CategoryId,
    string CategoryName,
    string Title,
    string Summary,
    JsonElement BodyDoc,
    string BodyPlainText,
    string Status,
    long Importance,
    string? NewBadgeUntil,
    string? UpdatedBadgeUntil,
    bool IsHidden,
    string CreatedAt,
    string UpdatedAt,
    string CreatedByUserId,
    string CreatedByDisplayName,
    string UpdatedByUserId,
    string UpdatedByDisplayName,
    string? DeletedAt,
    ArticleMergeInfo? MergeInfo,
    IReadOnlyList<ArticleAttachmentSummary> Attachments,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> Procedures,
    IReadOnlyList<string> Cautions,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<RelatedArticleSummary> RelatedArticles);

public sealed record ArticleMergeInfo(
    string TargetArticleId,
    string TargetArticleTitle,
    string MergedAt);

public sealed record ArticleAttachmentSummary(
    string Id,
    string OriginalName,
    string MediaType,
    long ByteSize,
    string Sha256,
    string AltText,
    string AssetPath,
    string CreatedAt);

public sealed record RelatedArticleSummary(
    string Id,
    string Title,
    string Status,
    string? DeletedAt,
    bool IsMerged);

public sealed record ViewLogSnapshot(
    string ArticleId,
    string? SourceSearchLogId);

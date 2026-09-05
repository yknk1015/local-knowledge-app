using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeApp.Data;

public sealed record CsvExportResult(string DestinationPath, long ExportedCount);

public sealed record CsvImportPreviewRow(
    long Line,
    string Action,
    string? FaqManagementId,
    string Title,
    bool BodyWillBeReplaced,
    bool StaleUpdateWillOverwrite,
    IReadOnlyList<string> Messages);

public sealed record CsvImportPreview(
    string SourcePath,
    string FileSha256,
    long TotalRows,
    long CreateCount,
    long UpdateCount,
    long UnchangedCount,
    long BodyReplacementCount,
    long StaleOverwriteCount,
    long ErrorCount,
    IReadOnlyList<CsvImportPreviewRow> Rows);

public sealed record CsvImportResult(
    string SourcePath,
    string SafetyBackupPath,
    long CreatedCount,
    long UpdatedCount,
    long UnchangedCount);

public sealed record JsonEntityCounts(
    long Categories,
    long Articles,
    long Tags,
    long SynonymGroups,
    long Relations,
    long MergeRelations);

public sealed record JsonExportResult(string DestinationPath, JsonEntityCounts Counts);

public sealed record JsonImportPreview(
    string SourcePath,
    string FileSha256,
    JsonEntityCounts Counts,
    long CreateCount,
    long UpdateCount,
    long UnchangedCount,
    long ErrorCount,
    IReadOnlyList<string> Errors);

public sealed record JsonImportResult(
    string SourcePath,
    string SafetyBackupPath,
    long CreatedCount,
    long UpdatedCount,
    long UnchangedCount);

public sealed record ExportFaqCsvInput(string DestinationPath);

public sealed record ImportFaqCsvInput(string SourcePath, string ExpectedFileSha256);

public sealed record ExportJsonInput(string DestinationPath);

public sealed record ImportJsonInput(string SourcePath, string ExpectedFileSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KnowledgeJsonDocument
{
    public required int FormatVersion { get; init; }
    public required string ExportedAt { get; init; }
    public required IReadOnlyList<JsonCategoryData> Categories { get; init; }
    public required IReadOnlyList<JsonTagData> Tags { get; init; }
    public required IReadOnlyList<JsonSynonymGroupData> SynonymGroups { get; init; }
    public required IReadOnlyList<JsonArticleData> Articles { get; init; }
    public required IReadOnlyList<JsonArticleRelationData> Relations { get; init; }
    public required IReadOnlyList<JsonMergeRelationData> MergeRelations { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonCategoryData
{
    public required string Id { get; init; }
    public required string ManagementCode { get; init; }
    public string? ParentId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required long SortOrder { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonTagData
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonSynonymGroupData
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> Terms { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonArticleData
{
    public required string Id { get; init; }
    public required string ManagementCode { get; init; }
    public required string CategoryId { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;
    public required JsonElement BodyDoc { get; init; }
    public required string Status { get; init; }
    public required long Importance { get; init; }
    public string? NewBadgeUntil { get; init; }
    public string? UpdatedBadgeUntil { get; init; }
    public bool IsHidden { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public string? DeletedAt { get; init; }
    public IReadOnlyList<string> Symptoms { get; init; } = [];
    public IReadOnlyList<string> Causes { get; init; } = [];
    public IReadOnlyList<string> Targets { get; init; } = [];
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];
    public IReadOnlyList<string> Procedures { get; init; } = [];
    public IReadOnlyList<string> Cautions { get; init; } = [];
    public IReadOnlyList<string> TagIds { get; init; } = [];
    public IReadOnlyList<string> SearchTerms { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonArticleRelationData
{
    public required string SourceArticleId { get; init; }
    public required string TargetArticleId { get; init; }
    public required long SortOrder { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonMergeRelationData
{
    public required string SourceArticleId { get; init; }
    public required string TargetArticleId { get; init; }
    public required string SourceUpdatedAt { get; init; }
    public required string MergedAt { get; init; }
}

internal enum CsvRowAction
{
    Create,
    Update,
    Unchanged,
    Error
}

internal sealed record CsvImportPlanRow(
    long Line,
    CsvRowAction Action,
    string? FaqManagementId,
    string ArticleId,
    string CategoryId,
    string Title,
    string Summary,
    string BodyPlainText,
    string BodyDocumentJson,
    bool BodyWillBeReplaced,
    string Status,
    long Importance,
    string? NewBadgeUntil,
    string? UpdatedBadgeUntil,
    bool IsHidden,
    bool StaleUpdateWillOverwrite,
    IReadOnlyList<string> Messages);

internal sealed record ExistingCsvArticle(
    string ArticleId,
    string CategoryId,
    string Title,
    string Summary,
    string BodyPlainText,
    string BodyDocumentJson,
    string Status,
    long Importance,
    string? NewBadgeUntil,
    string? UpdatedBadgeUntil,
    bool IsHidden,
    string UpdatedAt,
    bool HasAttachments,
    bool IsMergeTarget);

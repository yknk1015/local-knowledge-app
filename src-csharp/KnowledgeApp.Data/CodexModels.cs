using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeApp.Data;

public static class CodexJson
{
    // serde_json 1.0.151 starts with a recursion budget of 128 and rejects
    // the container that consumes its final unit: the wire limit is 127.
    // Keep both readers and writers inside that shared Rust/C# boundary.
    public const int MaximumDepth = 127;
    internal const int ProposalBodyDepth = MaximumDepth - 2; // proposal -> faq -> bodyDoc
    internal const int DelegationBodyDepth = MaximumDepth - 3; // delegation -> articles -> article -> bodyDoc
    internal static JsonDocumentOptions DocumentOptions => new() { MaxDepth = MaximumDepth };

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MaximumDepth,
        WriteIndented = true
    };
}

public static class CodexProposalKinds
{
    public const string Create = "create";
    public const string Revise = "revise";
    public const string Merge = "merge";

    public static bool IsValid(string value) => value is Create or Revise or Merge;
    public static bool IsDelegation(string value) => value is Revise or Merge;
}

public sealed record CodexFaqProposal
{
    public required uint FormatVersion { get; init; }
    public required string RequestId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExchangeGeneration { get; init; }
    public string? SeriesId { get; init; }
    public required string CreatedAt { get; init; }
    public string ProposalKind { get; init; } = CodexProposalKinds.Create;
    public IReadOnlyList<CodexSourceArticle> SourceArticles { get; init; } = [];
    public required CodexFaqDraft Faq { get; init; }
    public IReadOnlyList<CodexCategoryCandidate> ExistingCategoryCandidates { get; init; } = [];
    public CodexNewCategoryProposal? NewCategoryProposal { get; init; }
}

public sealed record CodexFaqDraft
{
    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;
    public required JsonElement BodyDoc { get; init; }
    public long Importance { get; init; } = 1;
}

public sealed record CodexSourceArticle(
    [property: JsonRequired] string ArticleId,
    [property: JsonRequired] string SourceUpdatedAt);

public sealed record CodexCategoryCandidate(
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string CategoryPath,
    [property: JsonRequired] string Reason);

public sealed record CodexNewCategoryProposal
{
    public string? ParentCategoryId { get; init; }
    public string? ParentCategoryPath { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string Reason { get; init; }
}

public sealed record RejectedCodexProposal(string FileName, string Message);

public sealed record CodexProposalHistoryItem(
    long HistoryId,
    CodexFaqProposal Proposal,
    string Status,
    string ReceivedAt,
    string? DecidedAt,
    string? AcceptedArticleId,
    bool CanReopen);

public sealed record CodexProposalInbox(
    IReadOnlyList<CodexFaqProposal> Proposals,
    IReadOnlyList<CodexProposalHistoryItem> History,
    IReadOnlyList<RejectedCodexProposal> Rejected,
    string InboxPath,
    string CategoryCatalogPath);

public sealed record AcceptCodexProposalInput(
    string RequestId,
    string? CategoryId,
    bool CreateProposedCategory);

public sealed record AcceptCodexProposalResult(
    ArticleDetail Article,
    CategorySummary? CreatedCategory);

public sealed record CreateCodexDelegationInput(string Kind, IReadOnlyList<string> ArticleIds);

public sealed record CodexDelegationResult(string DelegationId, string Prompt, string FilePath);

public sealed record CodexMergeSourcePreview(
    string ArticleId,
    string Title,
    string? Status,
    string SourceUpdatedAt,
    string? CurrentUpdatedAt,
    string? DeletedAt,
    bool IsCurrent,
    bool IsMerged);

public sealed record CodexMergePublicationContext(
    string TargetArticleId,
    IReadOnlyList<CodexMergeSourcePreview> SourceArticles,
    bool CanMarkMerged,
    bool AllSourcesMerged);

public sealed record MarkCodexMergeSourcesResult(string TargetArticleId, long MarkedCount);

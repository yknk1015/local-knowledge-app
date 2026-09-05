using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.Data;

// Synthetic in-memory JSON and one owned temporary root only. No input paths.
var passed = 0;
try
{
    if (args.Length != 0) throw new ArgumentException("This test accepts no input paths.");
    _ = LegacySource.Resolve();
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 128 };
    foreach (var depth in new[] { 127, 128, 129 })
    {
        var raw = Nested(depth);
        var proposal = "{\"formatVersion\":2,\"requestId\":\"51000000-0000-4000-8000-000000000001\",\"createdAt\":\"2026-09-05T00:00:00Z\",\"faq\":{\"title\":\"Synthetic\",\"bodyDoc\":" + Nested(depth - 2) + "}}";
        var delegation = "{\"articles\":[{\"bodyDoc\":" + Nested(depth - 3) + "}]}";
        var valueOk = Accept(() => JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 128 }).Dispose());
        var proposalOk = Accept(() => JsonSerializer.Deserialize<CodexFaqProposal>(proposal, options));
        var delegationOk = Accept(() => JsonSerializer.Deserialize<DepthDelegation>(delegation, options));
        Console.WriteLine($"CSHARP_CODEX_DEPTH {depth}: value={valueOk} proposal={proposalOk} delegation={delegationOk}");
        if (valueOk != (depth <= 128) || proposalOk != valueOk || delegationOk != valueOk) throw new InvalidOperationException("Unexpected depth semantics.");
        passed += 3;
    }
    Check(CodexJson.Options.MaxDepth == 127, "Production Codex JSON uses the measured shared depth127 boundary");
    var root = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}"));
    if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Synthetic root already exists.");
    try
    {
        using var database = KnowledgeDatabase.OpenSynthetic(root);
        File.WriteAllText(Path.Combine(root, "codex-interop.synthetic-marker"), "KnowledgeApp synthetic Codex cross-runtime v1", new System.Text.UTF8Encoding(false));
        var authentication = new AuthenticationService(database);
        authentication.Login("0000", "");
        var category = new ClassificationSearchService(database, authentication).CreateCategory("Synthetic", "Cross-runtime only", null);
        var editing = new ArticleEditingService(database, authentication);
        var service = new CodexProposalService(database, authentication);
        var files = new CodexProposalFiles(database);
        var originals = new Dictionary<string, CodexFaqProposal>(StringComparer.Ordinal);
        var originalHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bodyDepth in new[] { 77, 124 })
        {
            using var document = BodyAtDepth(bodyDepth);
            Check(ContainerDepth(document.RootElement) == bodyDepth, $"Synthetic stored body depth{bodyDepth}");
            var article = editing.SaveArticle(new(null, category.Id, $"Synthetic depth{bodyDepth}", "Synthetic only",
                document.RootElement, ArticleStatuses.Published, 1, null, null, false, [], [], [], [], [], [], [], [], []));
            var delegation = service.CreateDelegation(new("revise", [article.Id]));
            var proposal = new CodexFaqProposal
            {
                FormatVersion = 2,
                RequestId = Guid.CreateVersion7().ToString(),
                SeriesId = delegation.DelegationId,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                ProposalKind = "revise",
                SourceArticles = [new(article.Id, article.UpdatedAt)],
                Faq = new() { Title = article.Title, Summary = article.Summary, BodyDoc = document.RootElement.Clone(), Importance = 1 }
            };
            WriteOriginal(proposal);
            originalHashes.Add(delegation.FilePath, Hash(delegation.FilePath));
            using var delegated = JsonDocument.Parse(File.ReadAllBytes(delegation.FilePath), new JsonDocumentOptions { MaxDepth = 127 });
            Check(ContainerDepth(delegated.RootElement) == bodyDepth + 3, $"Real C# delegation depth{bodyDepth + 3}");
        }
        using (var boundary = BodyAtDepth(125))
        {
            var proposal = new CodexFaqProposal
            {
                FormatVersion = 2,
                RequestId = Guid.CreateVersion7().ToString(),
                SeriesId = Guid.CreateVersion7().ToString(),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Faq = new() { Title = "Synthetic proposal boundary", Summary = "Synthetic only", BodyDoc = boundary.RootElement.Clone(), Importance = 1 },
                NewCategoryProposal = new() { Name = "Synthetic proposed category", Reason = "Cross-runtime boundary test" }
            };
            WriteOriginal(proposal);
            Check(ContainerDepth(JsonSerializer.SerializeToElement(proposal, CodexJson.Options)) == 127, "C# emits a real full-depth127 proposal");
        }
        using (var tooDeep = BodyAtDepth(126))
        {
            var proposal = originals.Values.First(item => item.ProposalKind == "create") with
            { Faq = new() { Title = "Synthetic over boundary", BodyDoc = tooDeep.RootElement.Clone() } };
            Check(!Accept(() => JsonSerializer.Serialize(proposal, CodexJson.Options)), "C# serializer rejects full-depth128 proposal");
        }
        await RunRustRoundtrip(root);
        Check(originalHashes.All(item => Hash(item.Key) == item.Value), "Rust did not modify any C# source proposal or delegation bytes");
        var replies = JsonSerializer.Deserialize<Reply[]>(File.ReadAllText(Path.Combine(root, "codex-interop.synthetic-result.json")), options)!;
        Check(replies.Length == 3 && replies.Select(item => item.ReplyRequestId).Distinct().Count() == 3, "Three distinct Rust responses returned");
        var inbox = service.ListProposals();
        Check(inbox.Proposals.Count == 6 && inbox.Rejected.Count == 0, "C# service accepts every C# and Rust proposal");
        foreach (var reply in replies)
        {
            var original = originals[reply.OriginalRequestId];
            var proposal = inbox.Proposals.Single(item => item.RequestId == reply.ReplyRequestId);
            Check(JsonElement.DeepEquals(original.Faq.BodyDoc, proposal.Faq.BodyDoc), "Rust response preserves the complete deep body");
            files.ValidateDelegatedSources(proposal);
            var result = service.AcceptProposal(new(proposal.RequestId, original.ProposalKind == "create" ? category.Id : null, false));
            Check(JsonElement.DeepEquals(original.Faq.BodyDoc, result.Article.BodyDoc), "C# approval preserves the Rust-returned body");
        }
        Check(originalHashes.All(item => Hash(item.Key) == item.Value), "Approvals also retain all original input files");

        void WriteOriginal(CodexFaqProposal proposal)
        {
            var path = Path.Combine(files.InboxPath, proposal.RequestId + CodexProposalFiles.ProposalSuffix);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(proposal, CodexJson.Options));
            originals.Add(proposal.RequestId, proposal);
            originalHashes.Add(path, Hash(path));
        }
    }
    finally { FileSystemBoundary.DeleteSyntheticRoot(root); }
    Console.WriteLine($"CodexInteropCheck: {passed} checks passed; synthetic C# / Rust service roundtrip included.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("CodexInteropCheck failed: " + exception);
    return 1;
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine("PASS: " + message);
}

static JsonDocument BodyAtDepth(int depth)
{
    JsonNode leaf = new JsonObject { ["type"] = "text", ["text"] = "Synthetic deep answer" };
    switch ((depth - 5) % 4)
    {
        case 1: leaf["marks"] = new JsonArray(); break;
        case 2: leaf["marks"] = new JsonArray(new JsonObject { ["type"] = "bold" }); break;
        case 3: leaf["marks"] = new JsonArray(new JsonObject { ["type"] = "bold", ["attrs"] = new JsonObject() }); break;
    }
    JsonNode child = new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(leaf) };
    for (var index = 0; index < (depth - 5) / 4; index++)
        child = new JsonObject { ["type"] = "bulletList", ["content"] = new JsonArray(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph" }, child) }) };
    var root = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(child) };
    return JsonDocument.Parse(root.ToJsonString(new JsonSerializerOptions { MaxDepth = 144 }), new JsonDocumentOptions { MaxDepth = 144 });
}

static int ContainerDepth(JsonElement value) => value.ValueKind switch
{
    JsonValueKind.Object => 1 + value.EnumerateObject().Select(item => ContainerDepth(item.Value)).DefaultIfEmpty().Max(),
    JsonValueKind.Array => 1 + value.EnumerateArray().Select(ContainerDepth).DefaultIfEmpty().Max(),
    _ => 0
};

static async Task RunRustRoundtrip(string root)
{
    var info = new ProcessStartInfo("cargo")
    {
        WorkingDirectory = LegacySource.Resolve(),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[] { "test", "--offline", "codex_interop_csharp_roundtrip", "--", "--ignored", "--nocapture" }) info.ArgumentList.Add(argument);
    info.Environment["KNOWLEDGEAPP_CODEX_INTEROP_ROOT"] = root;
    using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the fixed Rust synthetic test.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Console.Write(await stdout);
    Console.Write(await stderr);
    if (process.ExitCode != 0) throw new InvalidOperationException("Rust synthetic cross-runtime test failed.");
}

static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

static string Nested(int depth) => new string('[', depth) + "0" + new string(']', depth);
static bool Accept(Action action) { try { action(); return true; } catch (JsonException) { return false; } }
internal sealed record DepthDelegation(IReadOnlyList<DepthArticle> Articles);
internal sealed record DepthArticle(JsonElement BodyDoc);
internal sealed record Reply(string OriginalRequestId, string ReplyRequestId);

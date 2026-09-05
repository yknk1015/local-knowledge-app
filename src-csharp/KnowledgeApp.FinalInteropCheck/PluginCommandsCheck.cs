using System.Diagnostics;
using System.Text.Json;
using KnowledgeApp.Data;
using KnowledgeApp.Mail;

internal static partial class FinalInteropCheck
{
    private static async Task CheckPluginCommands(string shell)
    {
        Console.WriteLine("PLUGIN_RUNTIME: " + Path.GetFileName(shell));
        var root = NewRoot();
        using var database = KnowledgeDatabase.OpenSynthetic(root);
        var authentication = new AuthenticationService(database);
        authentication.Login("0000", "");
        var category = new ClassificationSearchService(database, authentication).CreateCategory("合成プラグイン結合", "実データ不使用", null);
        var editing = new ArticleEditingService(database, authentication);
        var view = new ArticleViewService(database, authentication);
        var service = new CodexProposalService(database, authentication);
        var files = new CodexProposalFiles(database);
        using var sourceBody = Body("2026-08-12T00:00:00.1234567Z");
        using var excludedBody = Body("UNSELECTED_SYNTHETIC_SENTINEL");
        var source = editing.SaveArticle(Input(null, "合成の選択済みFAQ", sourceBody.RootElement));
        var excluded = editing.SaveArticle(Input(null, "未選択の合成FAQ", excludedBody.RootElement));
        var delegation = service.CreateDelegation(new("revise", [source.Id]));
        var delegationHash = Hash(delegation.FilePath);

        // Plugin test hooks do not accept Data's synthetic root name. Mirror only
        // the explicitly generated single delegation into an owned child root;
        // never copy a database, unselected FAQ, images, history or real mail.
        // The parent is already a fresh UUID owner. Keep this fixed child short
        // enough for Windows PowerShell 5.1 / .NET Framework MAX_PATH behavior.
        var pluginRoot = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "knowledgeapp-plugin-test-commands"));
        var delegationFolder = Path.Combine(pluginRoot, "codex-bridge", "delegations");
        var inbox = Path.Combine(pluginRoot, "codex-inbox");
        Directory.CreateDirectory(delegationFolder);
        Directory.CreateDirectory(inbox);
        var mirror = Path.Combine(delegationFolder, delegation.DelegationId + ".knowledge-delegation.json");
        File.Copy(delegation.FilePath, mirror, false);
        Check(Directory.GetFiles(pluginRoot, "*", SearchOption.AllDirectories).SequenceEqual([mirror]), "Plugin sandbox contains exactly the explicitly delegated file, not a DB or unselected content");

        var get = await RunPlugin(shell, "get-delegation.ps1", pluginRoot, delegation.DelegationId);
        Check(get.ExitCode == 0, "Real plugin get-delegation command accepts C# delegation in explicit test mode");
        if (get.ExitCode != 0) throw new InvalidOperationException(get.Error);
        using var read = JsonDocument.Parse(get.Output);
        var readSource = read.RootElement.GetProperty("articles").EnumerateArray().Single();
        Check(read.RootElement.GetProperty("delegationId").GetString() == delegation.DelegationId && readSource.GetProperty("articleId").GetString() == source.Id, "Plugin command reads only the requested delegation and selected source ID");
        Check(JsonElement.DeepEquals(readSource.GetProperty("bodyDoc"), source.BodyDoc), "Plugin output preserves Japanese source body");
        Check(readSource.GetProperty("sourceUpdatedAt").GetString() == source.UpdatedAt, "Plugin get command preserves the exact timestamp string, not just the instant");
        Check(get.Output.TrimEnd('\r', '\n') == File.ReadAllText(mirror, Utf8).TrimEnd('\r', '\n'), "Plugin get returns all original JSON text without timestamp or deep-body normalization");
        Check(!get.Output.Contains(excluded.Id, StringComparison.Ordinal) && !get.Output.Contains("UNSELECTED_SYNTHETIC_SENTINEL", StringComparison.Ordinal), "Plugin output excludes the unselected synthetic FAQ");
        Check(Hash(mirror) == delegationHash && Hash(delegation.FilePath) == delegationHash, "Reading through plugin leaves source delegation files byte-identical");

        await CheckCategoryMailAndMissingRoutes(shell, root, pluginRoot, database, authentication, delegation.DelegationId, originalDelegation: File.ReadAllText(mirror, Utf8));

        var originalDelegation = File.ReadAllText(mirror, Utf8);
        var paddedDelegation = originalDelegation + new string(' ', 5 * 1024 * 1024 - Utf8.GetByteCount(originalDelegation));
        File.WriteAllText(mirror, paddedDelegation, Utf8);
        var delegationAtLimit = await RunPlugin(shell, "get-delegation.ps1", pluginRoot, delegation.DelegationId);
        Check(delegationAtLimit.ExitCode == 0 && delegationAtLimit.Output.TrimEnd('\r', '\n') == paddedDelegation, "Plugin preserves a complete delegation at the 5 MiB UTF-8 boundary");
        File.WriteAllText(mirror, paddedDelegation + " ", Utf8);
        var delegationOverLimit = await RunPlugin(shell, "get-delegation.ps1", pluginRoot, delegation.DelegationId);
        Check(delegationOverLimit.ExitCode != 0 && string.IsNullOrEmpty(delegationOverLimit.Output), "Plugin refuses a delegation above 5 MiB before emitting its content");
        File.WriteAllText(mirror, originalDelegation, Utf8);
        Check(Hash(mirror) == delegationHash && Hash(delegation.FilePath) == delegationHash, "Boundary checks leave the original selected C# delegation untouched");

        using var revisedBody = Body("合成プラグインから返した修正案です。2026-08-12T00:00:00.1234567Z");
        var proposal = new CodexFaqProposal
        {
            FormatVersion = 2, RequestId = Guid.CreateVersion7().ToString(), SeriesId = delegation.DelegationId,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"), ProposalKind = "revise",
            SourceArticles = [new(source.Id, source.UpdatedAt)],
            Faq = new() { Title = "合成コマンド結合で修正したFAQ", Summary = "承認後だけ反映します。", BodyDoc = revisedBody.RootElement.Clone(), Importance = 2 }
        };
        var submitted = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, proposal: proposal);
        if (submitted.ExitCode != 0) throw new InvalidOperationException(submitted.Error);
        Check(submitted.Output.Contains(proposal.RequestId, StringComparison.Ordinal), "Real plugin submit command writes a pending proposal");
        var proposalPath = Path.Combine(inbox, proposal.RequestId + CodexProposalFiles.ProposalSuffix);
        Check(File.Exists(proposalPath) && !Directory.EnumerateFiles(inbox, "*.partial").Any(), "Plugin atomically finishes a single proposal without leftover partial files");
        Check(File.ReadAllText(proposalPath, Utf8) == JsonSerializer.Serialize(proposal, CodexJson.Options), "Plugin submit preserves exact JSON including source timestamp, IDs and unknown-field evidence");
        var proposalHash = Hash(proposalPath);
        var duplicate = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, proposal: proposal);
        Check(duplicate.ExitCode != 0 && Hash(proposalPath) == proposalHash, "Duplicate request ID is refused without overwriting its existing proposal");
        var limitProposal = proposal with { RequestId = Guid.CreateVersion7().ToString() };
        var limitJson = JsonSerializer.Serialize(limitProposal, CodexJson.Options);
        limitJson += new string(' ', 1024 * 1024 - Utf8.GetByteCount(limitJson));
        var atLimit = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, rawProposal: limitJson);
        var limitPath = Path.Combine(inbox, limitProposal.RequestId + CodexProposalFiles.ProposalSuffix);
        Check(atLimit.ExitCode == 0 && File.ReadAllText(limitPath, Utf8) == limitJson, "Plugin preserves a proposal at the exact 1 MiB UTF-8 boundary");
        var overId = Guid.CreateVersion7().ToString();
        var overJson = limitJson.Replace(limitProposal.RequestId, overId, StringComparison.Ordinal) + " ";
        var aboveLimit = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, rawProposal: overJson);
        Check(aboveLimit.ExitCode != 0 && !File.Exists(Path.Combine(inbox, overId + CodexProposalFiles.ProposalSuffix)), "Plugin refuses a proposal above 1 MiB without a final file");
        Check(Hash(proposalPath) == proposalHash && !Directory.EnumerateFiles(inbox, "*.partial").Any(), "Rejected oversized submission preserves prior proposals and leaves no partial files");
        File.Copy(proposalPath, Path.Combine(files.InboxPath, Path.GetFileName(proposalPath)), false);
        var pending = service.ListProposals();
        if (pending.Rejected.Count != 0) throw new InvalidOperationException("Synthetic plugin proposal rejected: " + string.Join("; ", pending.Rejected.Select(item => item.Message)));
        Check(pending.Proposals.Single().RequestId == proposal.RequestId && pending.Rejected.Count == 0, "C# accepts the proposal emitted by the real plugin command");
        Check(JsonElement.DeepEquals(view.GetArticle(source.Id).BodyDoc, source.BodyDoc), "Submitting and listing never auto-apply a revision");
        var accepted = service.AcceptProposal(new(proposal.RequestId, null, false)).Article;
        Check(accepted.Id == source.Id && accepted.Status == source.Status && accepted.CategoryId == source.CategoryId, "Explicit C# revision approval retains the source identity, status and category");
        Check(JsonElement.DeepEquals(accepted.BodyDoc, revisedBody.RootElement), "Explicit C# approval applies the plugin-returned body");
        Check(JsonElement.DeepEquals(view.GetArticle(excluded.Id).BodyDoc, excludedBody.RootElement), "Approval does not modify the unselected synthetic FAQ");
        Check(service.ListProposals().History.Single().Status == "accepted", "The approved real-command proposal remains in C# history");
        Check(Hash(delegation.FilePath) == delegationHash && Hash(mirror) == delegationHash, "Approval does not rewrite source delegation files");

        // A newer local edit must still win over a later-arriving old proposal.
        var staleDelegation = service.CreateDelegation(new("revise", [accepted.Id]));
        File.Copy(staleDelegation.FilePath, Path.Combine(delegationFolder, staleDelegation.DelegationId + ".knowledge-delegation.json"), false);
        var staleProposal = proposal with { RequestId = Guid.CreateVersion7().ToString(), SeriesId = staleDelegation.DelegationId, SourceArticles = [new(accepted.Id, accepted.UpdatedAt)] };
        var staleSubmit = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, proposal: staleProposal);
        Check(staleSubmit.ExitCode == 0, "Real plugin command can deliver a proposal before local version checking");
        var stalePath = Path.Combine(inbox, staleProposal.RequestId + CodexProposalFiles.ProposalSuffix);
        File.Copy(stalePath, Path.Combine(files.InboxPath, Path.GetFileName(stalePath)), false);
        using var localBody = Body("委譲の後に利用者が更新した合成本文です。");
        var local = editing.SaveArticle(Input(accepted.Id, "新しいローカル編集", localBody.RootElement));
        Check(local.UpdatedAt != accepted.UpdatedAt, "The synthetic local edit has a newer source version");
        _ = service.ListProposals();
        ExpectProblem(() => service.AcceptProposal(new(staleProposal.RequestId, null, false)), "CDX-012", "C# refuses stale plugin revision at explicit approval");
        Check(JsonElement.DeepEquals(view.GetArticle(source.Id).BodyDoc, local.BodyDoc), "Stale proposal rejection preserves the newer local content");

        foreach (var invalidKind in new[] { "unknown", "duplicate" })
        {
            var invalidProposal = proposal with { RequestId = Guid.CreateVersion7().ToString() };
            var raw = JsonSerializer.Serialize(invalidProposal, CodexJson.Options);
            raw = invalidKind == "unknown"
                ? raw.Insert(1, "\"unrecognizedSyntheticProperty\":true,")
                : raw.Insert(1, "\"formatVersion\":2,");
            var invalidSubmit = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, rawProposal: raw);
            var invalidPath = Path.Combine(inbox, invalidProposal.RequestId + CodexProposalFiles.ProposalSuffix);
            if (invalidSubmit.ExitCode == 0)
            {
                Check(File.ReadAllText(invalidPath, Utf8) == raw, $"Plugin does not erase {invalidKind} property evidence during validation");
                File.Copy(invalidPath, Path.Combine(files.InboxPath, Path.GetFileName(invalidPath)), false);
                Check(service.ListProposals().Rejected.Any(item => item.FileName == Path.GetFileName(invalidPath)), $"C# strict reader rejects plugin-carried {invalidKind} properties");
            }
            else
            {
                Check(!File.Exists(invalidPath), $"Plugin runtime itself rejects {invalidKind} properties without writing a proposal");
            }
        }
        Check(JsonElement.DeepEquals(view.GetArticle(source.Id).BodyDoc, local.BodyDoc), "Invalid JSON evidence never changes the current FAQ");

        var createId = Guid.CreateVersion7().ToString();
        var createProposal = proposal with
        {
            RequestId = createId, SeriesId = createId, ProposalKind = "create", SourceArticles = [],
            ExistingCategoryCandidates = [new(category.Id, category.Name, "合成の既存分類を使用")]
        };
        var createSubmit = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, proposal: createProposal);
        if (createSubmit.ExitCode != 0) throw new InvalidOperationException(createSubmit.Error);
        Check(createSubmit.Output.Contains(createId, StringComparison.Ordinal), "Real plugin command delivers a new FAQ proposal using only a category choice");
        var createFile = createId + CodexProposalFiles.ProposalSuffix;
        File.Copy(Path.Combine(inbox, createFile), Path.Combine(files.InboxPath, createFile), false);
        Check(service.ListProposals().Proposals.Any(item => item.RequestId == createId), "C# lists the plugin new FAQ proposal without automatically approving it");
        var created = service.AcceptProposal(new(createId, category.Id, false)).Article;
        Check(created.Status == ArticleStatuses.Draft && created.Id != source.Id && created.Id != excluded.Id, "Explicit new-proposal approval creates a separate unpublished draft");

        var mergeDelegation = service.CreateDelegation(new("merge", [local.Id, excluded.Id]));
        File.Copy(mergeDelegation.FilePath, Path.Combine(delegationFolder, mergeDelegation.DelegationId + ".knowledge-delegation.json"), false);
        var mergeGet = await RunPlugin(shell, "get-delegation.ps1", pluginRoot, mergeDelegation.DelegationId);
        Check(mergeGet.ExitCode == 0, "Real plugin command reads the explicitly selected merge delegation");
        using var mergeRead = JsonDocument.Parse(mergeGet.Output);
        Check(mergeRead.RootElement.GetProperty("articles").EnumerateArray().Select(item => item.GetProperty("articleId").GetString()).ToHashSet().SetEquals([local.Id, excluded.Id]), "Merge command output includes exactly the two explicitly selected FAQs");
        var mergeProposal = createProposal with
        {
            RequestId = Guid.CreateVersion7().ToString(), SeriesId = mergeDelegation.DelegationId, ProposalKind = "merge",
            SourceArticles = [new(local.Id, local.UpdatedAt), new(excluded.Id, excluded.UpdatedAt)]
        };
        var mergeSubmit = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, proposal: mergeProposal);
        if (mergeSubmit.ExitCode != 0) throw new InvalidOperationException(mergeSubmit.Error);
        Check(mergeSubmit.Output.Contains(mergeProposal.RequestId, StringComparison.Ordinal), "Real plugin command delivers the two-source merge proposal");
        var mergeFile = mergeProposal.RequestId + CodexProposalFiles.ProposalSuffix;
        File.Copy(Path.Combine(inbox, mergeFile), Path.Combine(files.InboxPath, mergeFile), false);
        Check(service.ListProposals().Proposals.Any(item => item.RequestId == mergeProposal.RequestId), "C# lists the plugin merge proposal as pending");
        var merged = service.AcceptProposal(new(mergeProposal.RequestId, category.Id, false)).Article;
        Check(merged.Status == ArticleStatuses.Draft && merged.Id != local.Id && merged.Id != excluded.Id, "Explicit merge approval creates a new unpublished draft");
        Check(JsonElement.DeepEquals(view.GetArticle(local.Id).BodyDoc, local.BodyDoc) && JsonElement.DeepEquals(view.GetArticle(excluded.Id).BodyDoc, excluded.BodyDoc), "Plugin merge approval leaves both original FAQ bodies unchanged");
        Check(view.GetArticle(local.Id).Status == local.Status && view.GetArticle(excluded.Id).Status == excluded.Status, "Plugin merge approval does not auto-publish, retire or delete original FAQs");

        SaveArticleInput Input(string? id, string title, JsonElement body) => new(id, category.Id, title, "合成概要", body,
            ArticleStatuses.Published, 1, null, null, false, [], [], [], [], [], [], [], [], []);
    }

    private static Task<(int ExitCode, string Output, string Error)> RunPlugin(string shell, string file, string root, string? delegationId = null, CodexFaqProposal? proposal = null, string? rawProposal = null)
    {
        if (file is not ("get-category-catalog.ps1" or "get-delegation.ps1" or "get-mail-delegation.ps1" or "submit-faq-proposal.ps1")) throw new ArgumentException("Unknown fixed plugin command.");
        FileSystemBoundary.ValidatePath(root, allowUnc: false);
        var info = new ProcessStartInfo(shell) { WorkingDirectory = Checkout() };
        // Process-only policy for the repository-owned test wrapper; never
        // change a persisted scope. Windows Group Policy still takes priority.
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Checkout(), "src-csharp", "KnowledgeApp.FinalInteropCheck", "Invoke-PluginCommand.ps1"), "-CommandName", file, "-TestDataRoot", root }) info.ArgumentList.Add(argument);
        if (delegationId is not null) { info.ArgumentList.Add("-DelegationId"); info.ArgumentList.Add(delegationId); }
        info.Environment["KNOWLEDGEAPP_PLUGIN_TEST_MODE"] = "1";
        return Run(info, rawProposal ?? (proposal is null ? null : JsonSerializer.Serialize(proposal, CodexJson.Options)));
    }
}

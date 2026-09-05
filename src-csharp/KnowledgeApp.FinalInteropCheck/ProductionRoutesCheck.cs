using System.Text.Json;
using KnowledgeApp.Data;
using KnowledgeApp.Mail;

internal static partial class FinalInteropCheck
{
    private static readonly string[] PluginCommandNames =
        ["get-category-catalog.ps1", "get-delegation.ps1", "get-mail-delegation.ps1", "submit-faq-proposal.ps1"];

    private static void CheckProductionRouteContracts()
    {
        // Resolve strings only: this test never probes either real user-data root.
        var localFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        const string legacyId = "jp.local.webknowledgesystem";
        if (_withLegacy)
        {
            using var tauriConfiguration = JsonDocument.Parse(File.ReadAllText(Path.Combine(LegacySource.Resolve(), "tauri.conf.json"), Utf8));
            Check(tauriConfiguration.RootElement.GetProperty("identifier").GetString() == legacyId,
                "Explicit archived Tauri source retains its independent rollback identifier");
        }
        Check(ProductionDataRoot.DirectoryName == "jp.local.webknowledgesystem.csharp" &&
            ProductionDataRoot.FixedPath == Path.Combine(localFolder, ProductionDataRoot.DirectoryName),
            "C# production resolves its new fixed Known Folder destination without opening user data");
        Check(ProductionDataRoot.FixedPath != Path.Combine(localFolder, legacyId) &&
            ProductionDataRoot.FixedPath != RehearsalDataRoot.FixedPath,
            "C# production is disjoint from both legacy Tauri and C# rehearsal data roots");
        Check(MailDelegationWriter.ProductionDataRootPath == ProductionDataRoot.FixedPath,
            "Default mail delegation and C# production database use the same new fixed root");

        foreach (var name in PluginCommandNames)
        {
            var source = File.ReadAllText(Path.Combine(Checkout(), "codex-plugins", "knowledgeapp-faq", "scripts", name), Utf8);
            Check(source.Contains("$dataRoot = Join-Path $localDataFolder '" + ProductionDataRoot.DirectoryName + "'", StringComparison.Ordinal) &&
                source.Contains("[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)", StringComparison.Ordinal) &&
                !source.Contains("$env:LOCALAPPDATA", StringComparison.OrdinalIgnoreCase) &&
                !source.Contains("'" + legacyId + "'", StringComparison.Ordinal) &&
                !source.Contains("'jp.local.webknowledgesystem.csharp-rehearsal'", StringComparison.Ordinal),
                $"{name} routes only to C# Known Folder storage, with no environment override or legacy fallback");
        }
    }

    private static async Task CheckCategoryMailAndMissingRoutes(
        string shell, string owner, string pluginRoot, KnowledgeDatabase database,
        AuthenticationService authentication, string delegationId, string originalDelegation)
    {
        var categories = new ClassificationSearchService(database, authentication).ListCategories();
        var files = new CodexProposalFiles(database);
        files.WriteCategoryCatalog(categories);
        var catalogPath = Path.Combine(pluginRoot, "codex-bridge", "categories.json");
        File.Copy(files.CategoryCatalogPath, catalogPath, false);
        var categoryHash = Hash(catalogPath);
        var getCatalog = await RunPlugin(shell, "get-category-catalog.ps1", pluginRoot);
        Check(getCatalog.ExitCode == 0, "Real plugin catalog command accepts a generated C# category catalog");
        using var catalog = JsonDocument.Parse(getCatalog.Output);
        Check(catalog.RootElement.GetProperty("categories").GetArrayLength() == categories.Count &&
            !getCatalog.Output.Contains("UNSELECTED_SYNTHETIC_SENTINEL", StringComparison.Ordinal) &&
            !getCatalog.Output.Contains("bodyDoc", StringComparison.Ordinal),
            "Catalog command exposes category information only, without FAQ content");
        Check(Hash(catalogPath) == categoryHash, "Reading the category catalog preserves its exact source bytes");

        var selectedMail = new MailPreviewItem(@"C:\synthetic-not-opened\selected.msg", "selected.msg", "合成C#メール委譲", "マスク済み送信者", "マスク済み宛先", DateTimeOffset.Parse("2026-09-06T00:00:00Z"), "明示選択した合成メールだけです。") { IsSelected = true };
        var excludedMail = new MailPreviewItem(@"C:\synthetic-not-opened\excluded.msg", "excluded.msg", "未選択", "未選択", "未選択", null, "UNSELECTED_SYNTHETIC_MAIL_SENTINEL") { IsSelected = false };
        var writer = new MailDelegationWriter(() => pluginRoot);
        var mail = writer.WriteSelected([selectedMail, excludedMail]);
        var mailPath = Path.Combine(pluginRoot, "codex-bridge", "mail-delegations", mail.DelegationId + ".knowledge-mail-delegation.json");
        var mailHash = Hash(mailPath);
        var getMail = await RunPlugin(shell, "get-mail-delegation.ps1", pluginRoot, mail.DelegationId.ToString());
        Check(getMail.ExitCode == 0, "Real plugin mail command accepts a C# selected-and-confirmed synthetic mail delegation");
        using var mailDocument = JsonDocument.Parse(getMail.Output);
        Check(mailDocument.RootElement.GetProperty("mails").GetArrayLength() == 1 &&
            mailDocument.RootElement.GetProperty("mails")[0].GetProperty("bodyText").GetString() == selectedMail.BodyText,
            "Mail command returns precisely the selected mail body");
        Check(!getMail.Output.Contains("UNSELECTED_SYNTHETIC_MAIL_SENTINEL", StringComparison.Ordinal) &&
            !getMail.Output.Contains("synthetic-not-opened", StringComparison.Ordinal) &&
            !getMail.Output.Contains("selected.msg", StringComparison.Ordinal),
            "Mail command excludes unselected mail, original paths, names and attachments");
        Check(Hash(mailPath) == mailHash, "Reading a selected mail delegation does not alter its source file");

        // These are fresh owned test files, not copies of any user's legacy data.
        // A matching old-style sibling must never satisfy a missing current file.
        var legacyRoot = Path.Combine(owner, "jp.local.webknowledgesystem");
        var legacyDelegations = Path.Combine(legacyRoot, "codex-bridge", "delegations");
        var legacyMail = Path.Combine(legacyRoot, "codex-bridge", "mail-delegations");
        var legacyInbox = Path.Combine(legacyRoot, "codex-inbox");
        Directory.CreateDirectory(legacyDelegations);
        Directory.CreateDirectory(legacyMail);
        Directory.CreateDirectory(legacyInbox);
        var missingId = Guid.NewGuid().ToString();
        var legacyDelegationPath = Path.Combine(legacyDelegations, missingId + ".knowledge-delegation.json");
        File.WriteAllText(legacyDelegationPath, originalDelegation.Replace(delegationId, missingId, StringComparison.Ordinal), Utf8);
        var legacyMailPath = Path.Combine(legacyMail, missingId + ".knowledge-mail-delegation.json");
        File.WriteAllText(legacyMailPath, File.ReadAllText(mailPath, Utf8).Replace(mail.DelegationId.ToString(), missingId, StringComparison.Ordinal), Utf8);
        var legacyCategoryPath = Path.Combine(legacyRoot, "codex-bridge", "categories.json");
        File.Copy(catalogPath, legacyCategoryPath, false);
        var oldDelegationHash = Hash(legacyDelegationPath);
        var oldMailHash = Hash(legacyMailPath);
        var absentFaq = await RunPlugin(shell, "get-delegation.ps1", pluginRoot, missingId);
        var absentMail = await RunPlugin(shell, "get-mail-delegation.ps1", pluginRoot, missingId);
        Check(absentFaq.ExitCode != 0 && string.IsNullOrEmpty(absentFaq.Output) && absentMail.ExitCode != 0 && string.IsNullOrEmpty(absentMail.Output),
            "Missing C# delegation IDs fail instead of searching matching synthetic legacy delegations");
        File.Delete(catalogPath);
        var absentCatalog = await RunPlugin(shell, "get-category-catalog.ps1", pluginRoot);
        Check(absentCatalog.ExitCode != 0 && string.IsNullOrEmpty(absentCatalog.Output), "Missing C# catalog never falls back to an old-style sibling catalog");
        File.Copy(files.CategoryCatalogPath, catalogPath, false);
        var currentInbox = Path.Combine(pluginRoot, "codex-inbox");
        Directory.Delete(currentInbox);
        var requestId = Guid.NewGuid().ToString();
        using var proposedBody = Body("旧提案箱へ送らない合成試験です。");
        var proposal = new CodexFaqProposal
        {
            FormatVersion = 2, RequestId = requestId, SeriesId = requestId, CreatedAt = DateTimeOffset.UtcNow.ToString("O"), ProposalKind = "create",
            Faq = new() { Title = "合成の提案箱不在", Summary = "実データ不使用", BodyDoc = proposedBody.RootElement.Clone(), Importance = 1 },
            ExistingCategoryCandidates = [new(categories[0].Id, categories[0].Name, "合成分類")]
        };
        var absentInbox = await RunPlugin(shell, "submit-faq-proposal.ps1", pluginRoot, proposal: proposal);
        Check(absentInbox.ExitCode != 0 && !Directory.Exists(currentInbox) && !Directory.EnumerateFileSystemEntries(legacyInbox).Any(),
            "Missing C# inbox rejects submission without recreating it or delivering to a legacy inbox");
        Directory.CreateDirectory(currentInbox);
        Check(Hash(legacyDelegationPath) == oldDelegationHash && Hash(legacyMailPath) == oldMailHash && Hash(legacyCategoryPath) == categoryHash,
            "Old-style synthetic catalog and pending delegations remain byte-identical and are never moved");
    }
}

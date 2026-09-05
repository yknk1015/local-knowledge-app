using System.Text.Json;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;

internal static class DispatcherTagCheck
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
        try
        {
            using var database = KnowledgeDatabase.OpenSynthetic(root);
            var authentication = new AuthenticationService(database);
            var attachments = new ArticleAttachmentService(root);
            var codex = new CodexProposalService(database, authentication, attachments);
            var dispatcher = new KnowledgeCommandDispatcher(
                authentication,
                new ClassificationSearchService(database, authentication),
                new ArticleViewService(database, authentication, attachments),
                new ArticleEditingService(database, authentication, attachments),
                new HistoryService(database, authentication),
                new TransferService(database, authentication, root),
                new BackupService(database, authentication, root),
                new SettingsService(database, authentication, root),
                codex,
                () => throw new InvalidOperationException("Unexpected image dialog"),
                _ => throw new InvalidOperationException("Unexpected save dialog"),
                _ => throw new InvalidOperationException("Unexpected open dialog"),
                _ => throw new InvalidOperationException("Unexpected clipboard access"),
                _ => throw new InvalidOperationException("Unexpected URL open"));

            var create = JsonSerializer.SerializeToElement(new { input = new { name = "合成配線確認" } });
            Expect("AUTH-002", () => dispatcher.Execute("save_tag", create));
            Expect("AUTH-002", () => dispatcher.Execute("delete_tag", JsonSerializer.SerializeToElement(new { id = Guid.NewGuid().ToString() })));
            authentication.Login("0000", string.Empty);
            var tag = dispatcher.Execute("save_tag", create) as TagMasterItem
                ?? throw new InvalidOperationException("save_tag did not return the tag contract");
            Check(tag.Name == "合成配線確認" && tag.UsageCount == 0, "save_tag response did not preserve fields");
            var renamed = dispatcher.Execute("save_tag", JsonSerializer.SerializeToElement(new { input = new { id = tag.Id, name = "合成配線変更" } })) as TagMasterItem;
            Check(renamed?.Id == tag.Id && renamed.Name == "合成配線変更", "save_tag did not route an existing ID");
            Check(dispatcher.Execute("delete_tag", JsonSerializer.SerializeToElement(new { id = tag.Id })) is null,
                "delete_tag must return a void result");
            var remaining = dispatcher.Execute("list_tags", JsonSerializer.SerializeToElement(new { })) as IReadOnlyList<TagMasterItem>;
            Check(remaining is not null && remaining.All(item => item.Id != tag.Id), "delete_tag did not delete the selected tag");
            Expect("SYS-001", () => dispatcher.Execute("save_tag", JsonSerializer.SerializeToElement(new { invalid = "input" })));
            Expect("SYS-001", () => dispatcher.Execute("delete_tag", JsonSerializer.SerializeToElement(new { id = 123 })));
            Expect("MIG-001", () => dispatcher.Execute("execute_sql", JsonSerializer.SerializeToElement(new { sql = "synthetic only" })));
            Console.WriteLine("PASS: tag commands are wired through the real C# dispatcher; UI/file delegates were never invoked");
        }
        finally
        {
            FileSystemBoundary.DeleteSyntheticRoot(root);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (AppProblemException exception) when (exception.Problem.Code == code) { return; }
        throw new InvalidOperationException($"Expected {code}");
    }
}

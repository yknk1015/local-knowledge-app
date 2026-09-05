using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeApp.Data;

// Synthetic manual-test data only; no existing database or input path is accepted.
internal static class ManualCodexDepthFixture
{
    internal const string Title = "合成多階層：深い本文をCodexへ委譲できますか？";
    internal const string CategoryName = "合成Codex本文互換テスト";
    internal const string Text = "合成の深い本文です。委譲準備の成功だけを確認し、実プラグインへは送信しません。";

    internal static GeneratedManualJson Create(Func<string> newRoot)
    {
        var databaseRoot = FreshRoot(newRoot());
        var outputRoot = FreshRoot(newRoot());
        Directory.CreateDirectory(outputRoot);
        using var database = KnowledgeDatabase.OpenSynthetic(databaseRoot);
        using (var connection = LegacyFixture.Open(database.OpenInfo.DatabasePath, readOnly: false))
        {
            if (LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM categories") != "0" ||
                LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM articles") != "0" ||
                LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM management_code_sequences WHERE next_value=1") != "2")
                throw new InvalidOperationException("Manual fixture requires a fresh empty synthetic database.");
            // Reserved for the reported post-C4 test baseline with management codes 1.
            // An unrelated database that already uses code 2 must still reject import.
            LegacyFixture.Execute(connection, "UPDATE management_code_sequences SET next_value=$next", ("$next", 2));
        }
        var authentication = new AuthenticationService(database);
        authentication.Login("0000", "");
        var category = new ClassificationSearchService(database, authentication).CreateCategory(CategoryName, "第15段階の合成手動検証専用", null);
        var node = JsonSerializer.Serialize(new { type = "paragraph", content = new[] { new { type = "text", text = Text } } });
        for (var index = 0; index < 18; index++)
            node = "{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[{\"type\":\"paragraph\",\"content\":[]}," + node + "]}]}";
        using var body = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + node + "]}", StructuredJsonBoundary.DocumentOptions);
        var article = new ArticleEditingService(database, authentication).SaveArticle(new(null, category.Id, Title,
            "従来の64階層制限を超える合成本文で、Codexへのローカル委譲準備を確認します。",
            body.RootElement, ArticleStatuses.Published, 1, null, null, false, [], [], [], [], [], [], [], [], []));
        var path = Path.Combine(outputRoot, "Synthetic_codex_deep_document.knowledge-export.json");
        var export = new TransferService(database, authentication, databaseRoot).ExportJson(new(path));
        return new(path, outputRoot, article.Id, category.Id, "FAQ-00002", "CAT-00002", export.Counts,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string FreshRoot(string root)
    {
        var validated = FileSystemBoundary.ValidateSyntheticRoot(root);
        if (Directory.Exists(validated) || File.Exists(validated)) throw new IOException("Refusing existing fixture root.");
        return validated;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeApp.Data;

// Test code only. The retest codes are selected for the reported C1 baseline
// (CAT-00001 / FAQ-00001), not as universally free IDs in arbitrary databases.
internal static class ManualLongDocumentFixture
{
    internal const string Title = "合成長文：末尾の編集と複製を確認できますか？";
    internal const string CategoryName = "合成長文互換テスト";
    internal const string Suffix = "長文編集テストの末尾です。";
    internal static string BodyText => new string('あ', 100_001 - Suffix.Length) + Suffix;

    internal static GeneratedManualJson CreateRetest(Func<string> newRoot) => Create(newRoot, retest: true);
    internal static GeneratedManualJson CreateOriginalCodesForRegression(Func<string> newRoot) => Create(newRoot, retest: false);

    private static GeneratedManualJson Create(Func<string> newRoot, bool retest)
    {
        var databaseRoot = FreshRoot(newRoot());
        var outputRoot = FreshRoot(newRoot());
        Directory.CreateDirectory(outputRoot);
        using var database = KnowledgeDatabase.OpenSynthetic(databaseRoot);
        if (retest)
        {
            // Only the newly generated empty test DB receives this fixture setup.
            // No user records, existing IDs, app guards or production API change.
            using var connection = LegacyFixture.Open(database.OpenInfo.DatabasePath, readOnly: false);
            if (LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM categories") != "0" ||
                LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM articles") != "0" ||
                LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM management_code_sequences WHERE next_value=1") != "2")
                throw new InvalidOperationException("Manual fixture requires a new empty synthetic database.");
            LegacyFixture.Execute(connection, "UPDATE management_code_sequences SET next_value=$next", ("$next", 2));
        }
        var authentication = new AuthenticationService(database); authentication.Login("0000", "");
        var category = new ClassificationSearchService(database, authentication).CreateCategory(CategoryName, "手動検証専用", null);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "doc",
            content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = BodyText } } } }
        }));
        var article = new ArticleEditingService(database, authentication).SaveArticle(new(null, category.Id,
            Title, "合成100001文字の末尾を編集し、保存後の本文と複製を確認するためのFAQです。",
            document.RootElement, ArticleStatuses.Published, 1, null, null, false, [], [], [], [], [], [], [], [], []));
        var name = retest ? "Synthetic_long_document_retest.knowledge-export.json" : "Synthetic_original_codes_regression.knowledge-export.json";
        var path = Path.Combine(outputRoot, name);
        var exported = new TransferService(database, authentication, databaseRoot).ExportJson(new(path));
        return new(path, outputRoot, article.Id, category.Id,
            retest ? "FAQ-00002" : "FAQ-00001", retest ? "CAT-00002" : "CAT-00001", exported.Counts,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string FreshRoot(string root)
    {
        var validated = FileSystemBoundary.ValidateSyntheticRoot(root);
        if (Directory.Exists(validated) || File.Exists(validated))
            throw new IOException("Manual fixture accepts only a newly generated synthetic root.");
        return validated;
    }
}

internal sealed record GeneratedManualJson(string Path, string OutputRoot, string ArticleId, string CategoryId,
    string ArticleCode, string CategoryCode, JsonEntityCounts Counts, string Sha256);

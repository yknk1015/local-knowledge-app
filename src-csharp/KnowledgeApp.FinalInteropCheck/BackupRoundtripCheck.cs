using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeApp.Data;

internal static partial class FinalInteropCheck
{
    private const string SyntheticUserLogin = "final-interop-user";
    // Explicit test fixture only; never the initial administrator emergency key.
    private const string SyntheticUserPassword = "Synthetic-FinalInterop-2026!";

    private static async Task CheckBackupRoundtrip()
    {
        var sourceRoot = NewRoot();
        using var sourceDatabase = KnowledgeDatabase.OpenSynthetic(sourceRoot);
        var sourceAuth = new AuthenticationService(sourceDatabase);
        sourceAuth.Login("0000", "");
        var user = sourceAuth.CreateUser(SyntheticUserLogin, "合成往復利用者", SyntheticUserPassword, "user");
        var categories = new ClassificationSearchService(sourceDatabase, sourceAuth);
        var category = categories.CreateCategory("合成最終往復", "C#とRustの正常利用範囲を比較", null);
        new SettingsService(sourceDatabase, sourceAuth, sourceRoot).SaveSettings(new AppSettings
        {
            ColorTheme = ColorThemes.Blue, ShowTopCategoryInTitle = false, ShowMascot = false
        });
        sourceAuth.Logout();
        sourceAuth.Login(SyntheticUserLogin, SyntheticUserPassword);
        var attachments = new ArticleAttachmentService(sourceRoot);
        // A generated/test-only one-pixel PNG, not an attachment from any user DB.
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5WQAAAAASUVORK5CYII=");
        var staged = attachments.StageBytes("合成最終往復.png", imageBytes);
        using var body = JsonDocument.Parse($$$"""
            {"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"合成最終往復：画像と通常本文を保持します。"}]},{"type":"image","attrs":{"attachmentId":"{{{staged.Id}}}","src":"knowledge-attachment:{{{staged.Id}}}","alt":"合成の1ピクセル画像"}}]}
            """);
        var article = new ArticleEditingService(sourceDatabase, sourceAuth, attachments).SaveArticle(new(
            null, category.Id, "合成最終往復のFAQ", "画像・認証・設定を保持します。", body.RootElement,
            ArticleStatuses.Published, 2, null, null, false, [], [], [], [], [], [], [], [], []));
        var searchId = categories.RecordSearchLog("合成最終往復", null, SearchScopes.All, 1);
        new ArticleViewService(sourceDatabase, sourceAuth, attachments).RecordArticleView(article.Id, searchId);
        Check(article.Attachments.Count == 1 && article.CreatedByUserId == user.Id, "C# creates a normal published image FAQ attributed to the synthetic user");
        sourceAuth.Logout();
        sourceAuth.Login("0000", "");
        var sourceBackup = Path.Combine(sourceRoot, "Synthetic_CSharp_to_Rust.faqbackup");
        var backup = new BackupService(sourceDatabase, sourceAuth, sourceRoot).CreateFullBackup(new(sourceBackup, "Synthetic C# to Rust", false));
        Check(backup.Counts == new BackupCounts(1, 1, 1, 0), "C# backup contains exactly one FAQ, category and managed image");
        var backupHash = Hash(sourceBackup);
        var imageHash = Convert.ToHexStringLower(SHA256.HashData(imageBytes));
        File.WriteAllText(Path.Combine(sourceRoot, "final-interop.synthetic-marker"), "KnowledgeApp final backup cross-runtime v1", Utf8);
        File.WriteAllText(Path.Combine(sourceRoot, "final-interop.synthetic-input.json"), JsonSerializer.Serialize(new
        {
            article, categoryId = category.Id, userId = user.Id, imageId = staged.Id, imageSha256 = imageHash,
            backupSha256 = backupHash, searchId
        }, CodexJson.Options), Utf8);
        var info = new ProcessStartInfo("cargo") { WorkingDirectory = LegacySource.Resolve() };
        foreach (var argument in new[] { "test", "--offline", "--lib", "final_interop_backup_csharp_roundtrip", "--", "--ignored", "--nocapture" }) info.ArgumentList.Add(argument);
        info.Environment["KNOWLEDGEAPP_FINAL_INTEROP_ROOT"] = sourceRoot;
        var rust = await Run(info);
        Console.Write(rust.Output);
        Console.Write(rust.Error);
        Check(rust.ExitCode == 0, "Actual Rust backup services restore C# data and create a return archive");
        Check(Hash(sourceBackup) == backupHash, "Rust leaves the selected C# backup byte-identical");
        var returnPath = Path.Combine(sourceRoot, "Synthetic_Rust_to_CSharp.faqbackup");
        var returnHash = Hash(returnPath);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceRoot, "final-interop.synthetic-result.json"), Utf8));
        var rustCategoryId = result.RootElement.GetProperty("rustCategoryId").GetString()!;

        var targetRoot = NewRoot();
        using var targetDatabase = KnowledgeDatabase.OpenSynthetic(targetRoot);
        var auth = new AuthenticationService(targetDatabase);
        auth.Login("0000", "");
        var targetBackup = new BackupService(targetDatabase, auth, targetRoot);
        var preview = targetBackup.InspectBackup(returnPath);
        Check(preview.SchemaVersion == 7 && preview.Counts == new BackupCounts(1, 2, 1, 0), "C# inspects the real Rust schema7 return archive and new Rust category");
        var restored = targetBackup.RestoreConfirmedBackup(new(returnPath, preview.ConfirmationToken));
        Check(auth.GetCurrentUser() is null && File.Exists(restored.SafetyBackupPath), "Confirmed C# restore logs out and protects the prior database with a safety backup");
        Check(Hash(returnPath) == returnHash && Hash(sourceBackup) == backupHash, "Both selected cross-runtime backups remain unchanged after restoration");
        auth.Login("0000", "");
        Check(auth.ListUsers().Any(item => item.Id == user.Id && item.LoginId == SyntheticUserLogin && item.Role == "user"), "C# restores the synthetic user identity and permission");
        var targetCategories = new ClassificationSearchService(targetDatabase, auth).ListCategories();
        Check(targetCategories.Any(item => item.Id == category.Id && item.Name == category.Name) && targetCategories.Any(item => item.Id == rustCategoryId && item.Name == "合成Rust往復追加分類"), "Original and Rust-created categories retain their internal identities");
        var settings = new SettingsService(targetDatabase, auth, targetRoot).GetSettings();
        Check(settings.ColorTheme == ColorThemes.Blue && !settings.ShowMascot && !settings.ShowTopCategoryInTitle, "Blue theme and display settings survive C# to Rust to C#");
        auth.Logout();
        var restoredUser = auth.Login(SyntheticUserLogin, SyntheticUserPassword);
        Check(restoredUser.Id == user.Id && restoredUser.Role == "user", "Original C# Argon2 user credentials still authenticate after Rust and C# restore");
        var restoredArticle = new ArticleViewService(targetDatabase, auth, new ArticleAttachmentService(targetRoot)).GetArticle(article.Id);
        Check(restoredArticle.Title == article.Title && restoredArticle.Summary == article.Summary && restoredArticle.Status == article.Status && restoredArticle.Importance == article.Importance, "FAQ title, summary, publication and importance survive the roundtrip");
        Check(JsonElement.DeepEquals(restoredArticle.BodyDoc, article.BodyDoc) && restoredArticle.BodyPlainText == article.BodyPlainText, "Normal rich JSON body and searchable text survive without loss");
        Check(restoredArticle.CreatedAt == article.CreatedAt && restoredArticle.UpdatedAt == article.UpdatedAt && restoredArticle.CreatedByUserId == user.Id && restoredArticle.UpdatedByUserId == user.Id, "FAQ dates and author/update audit identities remain intact");
        Check(restoredArticle.Attachments.Single().Id == staged.Id && restoredArticle.Attachments.Single().Sha256 == imageHash && restoredArticle.Attachments.Single().AltText == "合成の1ピクセル画像", "Managed image ID, digest and alternative text survive the roundtrip");
        var targetImage = Path.Combine(targetRoot, "attachments", "articles", article.Id, staged.Id + ".png");
        Check(Hash(targetImage) == imageHash, "Restored image bytes match the C# source image exactly");
        var history = new HistoryService(targetDatabase, auth);
        Check(history.ListSearchLogs(new("", null, null, false, 1)).Items.Single().Id == searchId, "Original explicit search history is retained");
        Check(history.ListViewLogs(new("", null, null, 1)).Items.Single().SourceQueryText == "合成最終往復", "Original view history retains its source search association");
    }
}

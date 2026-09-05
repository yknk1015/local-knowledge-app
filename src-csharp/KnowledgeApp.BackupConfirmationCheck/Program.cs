using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;

var roots = new List<string>();
var passed = 0;
try
{
    var archiveA = LegacyFixture.Create(NewRoot(), 7);
    var archiveB = LegacyFixture.Create(NewRoot(), 7, mutation: "UPDATE articles SET title='差替え先の合成FAQ' WHERE id='20000000-0000-4000-8000-000000000001'");
    var originalAHash = Hash(archiveA.Archive); var originalBHash = Hash(archiveB.Archive);
    Check(originalAHash != originalBHash, "two independently valid synthetic full archives differ in content");
    var files = NewRoot(); Directory.CreateDirectory(files);
    var selected = Path.Combine(files, "Selected_synthetic.faqbackup");
    var otherPath = Path.Combine(files, "Other_synthetic.faqbackup");
    File.Copy(archiveA.Archive, selected); File.Copy(archiveA.Archive, otherPath);
    var root = NewRoot();
    using var db = KnowledgeDatabase.OpenRehearsalForTest(root);
    var auth = new AuthenticationService(db); auth.Login("0000", "");
    auth.CreateUser("other-admin", "合成別管理者", "Synthetic-other", UserRoles.Admin);
    auth.CreateUser("ordinary-user", "合成一般利用者", "Synthetic-other", UserRoles.User);
    var attachments = new ArticleAttachmentService(root);
    var categories = new ClassificationSearchService(db, auth);
    var category = categories.CreateCategory("復元前の合成分類", "確認の拒否後も維持", null);
    var editing = new ArticleEditingService(db, auth, attachments);
    editing.SaveTag(null, "合成保持タグ");
    var image = LegacyFixture.CreatePng(); var stage = attachments.StageBytes("合成保持.png", image);
    using var body = JsonDocument.Parse($$$"""
        {"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"現在の合成FAQは拒否後も保持します。"}]},{"type":"image","attrs":{"attachmentId":"{{{stage.Id}}}","src":"knowledge-attachment:{{{stage.Id}}}","alt":"合成保持画像"}}]}
        """);
    var article = editing.SaveArticle(new SaveArticleInput(null, category.Id, "現在の合成FAQ", "変更拒否を確認します。", body.RootElement,
        ArticleStatuses.Published, 2, null, null, false, [], [], [], [], [], [], ["合成保持タグ"], [], []));
    var log = categories.RecordSearchLog("合成保持", category.Id, SearchScopes.Current, 1);
    new ArticleViewService(db, auth, attachments).RecordArticleView(article.Id, log);
    new SettingsService(db, auth, root).SaveSettings(new AppSettings { ColorTheme = ColorThemes.Blue, ShowMascot = false });
    var clock = new TestClock(); var callbackCount = 0;
    var backup = new BackupService(db, auth, root, () => callbackCount++, clock);
    var dispatcher = new KnowledgeCommandDispatcher(auth, categories, new ArticleViewService(db, auth, attachments), editing,
        new HistoryService(db, auth), new TransferService(db, auth, root), backup, new SettingsService(db, auth, root),
        new CodexProposalService(db, auth, attachments),
        () => throw new Exception("Unexpected dialog"), _ => throw new Exception("Unexpected save dialog"),
        _ => throw new Exception("Unexpected open dialog"), _ => throw new Exception("Unexpected clipboard"), _ => throw new Exception("Unexpected URL"));

    BackupPreview Preview() => dispatcher.Execute("inspect_backup", JsonSerializer.SerializeToElement(new { path = selected })) as BackupPreview
        ?? throw new Exception("The real dispatcher did not return a preview.");
    object? Restore(string? token, string? path = null) => dispatcher.Execute("restore_backup", JsonSerializer.SerializeToElement(new
        { input = new { path = path ?? selected, confirmationToken = token } }));
    void Refused(string label, string code, Action action)
    {
        using var beforeConnection = LegacyFixture.Open(db.OpenInfo.DatabasePath);
        var before = LegacyFixture.Snapshot(beforeConnection);
        var priorUser = auth.GetCurrentUser();
        var hashes = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => path, Hash);
        var sourceHash = Hash(selected); var priorCallbacks = callbackCount;
        Expect(code, action, label);
        using var afterConnection = LegacyFixture.Open(db.OpenInfo.DatabasePath);
        var after = LegacyFixture.Snapshot(afterConnection, before);
        Check(before.All(table => table.Value.Rows.SequenceEqual(after[table.Key].Rows)), label + " / every current DB table retained");
        Check(hashes.All(file => File.Exists(file.Key) && Hash(file.Key) == file.Value), label + " / current image, marker and managed files retained");
        Check(ReferenceEquals(priorUser, auth.GetCurrentUser()) && callbackCount == priorCallbacks, label + " / current session retained; no successful-restore callback");
        Check(Hash(selected) == sourceHash && Hash(archiveA.Archive) == originalAHash && Hash(archiveB.Archive) == originalBHash,
            label + " / selected and original archives unchanged");
    }

    var preview = Preview();
    Check(preview.ConfirmationToken is { Length: 64 } && preview.ConfirmationToken.All(Uri.IsHexDigit) &&
          !preview.ConfirmationToken.Contains("Selected", StringComparison.Ordinal), "dispatcher preview returns an opaque random confirmation, not a path or hash");
    Check(preview.SourcePath == Path.GetFullPath(selected) && preview.Counts == new BackupCounts(2, 2, 1, 1), "preview retains normalized path and visible counts");
    Refused("legacy path-only host request cannot bypass confirmation", "SYS-001",
        () => dispatcher.Execute("restore_backup", JsonSerializer.SerializeToElement(new { path = selected })));
    Refused("missing confirmation", "BK-011", () => Restore(null));
    Refused("unknown confirmation", "BK-011", () => Restore(new string('f', 64)));
    Refused("malformed confirmation", "BK-011", () => Restore("short"));
    Refused("different path despite identical archive bytes", "BK-011", () => Restore(preview.ConfirmationToken, otherPath));
    Refused("path mismatch consumed the token", "BK-011", () => Restore(preview.ConfirmationToken));

    preview = Preview();
    clock.Advance(TimeSpan.FromMinutes(10));
    Refused("10-minute expiration boundary", "BK-011", () => Restore(preview.ConfirmationToken));
    Refused("expired token cannot be reused", "BK-011", () => Restore(preview.ConfirmationToken));

    preview = Preview(); auth.Logout();
    Refused("logged out caller", "AUTH-002", () => Restore(preview.ConfirmationToken));
    auth.Login("0000", "");
    Refused("same user re-login is a distinct session", "BK-011", () => Restore(preview.ConfirmationToken));
    preview = Preview(); auth.Login("other-admin", "Synthetic-other");
    Refused("different administrator session", "BK-011", () => Restore(preview.ConfirmationToken));
    auth.Login("0000", ""); preview = Preview(); auth.Login("ordinary-user", "Synthetic-other");
    Refused("ordinary user cannot restore with a stolen preview", "AUTH-003", () => Restore(preview.ConfirmationToken));
    auth.Login("0000", "");

    preview = Preview();
    var otherService = new BackupService(db, auth, root);
    Refused("token belongs to the issuing host service only", "BK-011",
        () => otherService.RestoreConfirmedBackup(new(selected, preview.ConfirmationToken)));

    preview = Preview();
    File.Copy(archiveB.Archive, selected, overwrite: true);
    Refused("different valid archive substituted after preview", "BK-011", () => Restore(preview.ConfirmationToken));
    File.Copy(archiveA.Archive, selected, overwrite: true);
    Refused("restoring original bytes does not revive consumed token", "BK-011", () => Restore(preview.ConfirmationToken));

    var evicted = Preview().ConfirmationToken;
    for (var index = 0; index < 16; index++) { clock.Advance(TimeSpan.FromSeconds(1)); _ = Preview(); }
    Refused("bounded preview registry evicts the oldest entry", "BK-011", () => Restore(evicted));

    preview = Preview();
    var failurePath = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "safety-backups"));
    if (Directory.Exists(failurePath))
    {
        Check(!Directory.EnumerateFileSystemEntries(failurePath).Any(), "preflight refusals did not create safety backup files");
        Directory.Delete(failurePath, recursive: false);
    }
    File.WriteAllText(failurePath, "synthetic obstruction owned by this test", new UTF8Encoding(false));
    Refused("safety-backup preparation failure", "BK-010", () => Restore(preview.ConfirmationToken));
    File.Delete(failurePath);
    Refused("failed restore still consumes confirmation", "BK-011", () => Restore(preview.ConfirmationToken));

    // Pause a successful login between DB authentication and session publication.
    // A concurrent restore must wait, then reject the old session's token.
    preview = Preview();
    var oldSessionToken = preview.ConfirmationToken;
    using (var authenticated = new ManualResetEventSlim())
    using (var publishLogin = new ManualResetEventSlim())
    using (var restoreStarted = new ManualResetEventSlim())
    {
        auth.AuthenticatedForTest = () =>
        {
            authenticated.Set();
            if (!publishLogin.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic login release timeout.");
        };
        var loginTask = Task.Run(() => auth.Login("0000", ""));
        Task<Exception?>? restoreTask = null;
        try
        {
            Check(authenticated.Wait(TimeSpan.FromSeconds(5)), "concurrent login reached the controlled post-authentication boundary");
            restoreTask = Task.Run<Exception?>(() =>
            {
                restoreStarted.Set();
                try { Restore(oldSessionToken); return null; }
                catch (Exception exception) { return exception; }
            });
            Check(restoreStarted.Wait(TimeSpan.FromSeconds(5)), "concurrent restore request started while login publication was paused");
            Check(!restoreTask.Wait(TimeSpan.FromMilliseconds(200)), "restore waits for the in-flight authentication session lock");
        }
        finally { publishLogin.Set(); auth.AuthenticatedForTest = null; }
        var newSession = loginTask.GetAwaiter().GetResult();
        var restoreFailure = restoreTask!.GetAwaiter().GetResult();
        Check(restoreFailure is AppProblemException { Problem.Code: "BK-011" }, "login-first race rejects the old session confirmation after login completes");
        Check(ReferenceEquals(auth.GetCurrentUser(), newSession) && callbackCount == 0 &&
            new ArticleViewService(db, auth, attachments).GetArticle(article.Id).Title == "現在の合成FAQ",
            "login-first race retains current FAQ and the newly authenticated session");
    }

    // The hook is internal, per database instance, and cleared immediately. It
    // only attempts mutation of this test's generated archive after SHA match.
    var handleChecks = 0;
    Task<Exception?>? pendingOldLogin = null;
    using var oldLoginStarted = new ManualResetEventSlim();
    db.BackupSourceVerifiedForTest = () =>
    {
        try { using var writer = new FileStream(selected, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); throw new Exception("Write handle accepted."); }
        catch (IOException) { Check(true, "same verified source handle refuses concurrent writes"); handleChecks++; }
        try { File.Move(selected, selected + ".renamed"); throw new Exception("Rename accepted."); }
        catch (IOException) { Check(true, "same verified source handle refuses rename/replacement"); handleChecks++; }
        pendingOldLogin = Task.Run<Exception?>(() =>
        {
            oldLoginStarted.Set();
            try { auth.Login("0000", ""); return null; }
            catch (Exception exception) { return exception; }
        });
        if (!oldLoginStarted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic concurrent login did not start.");
    };
    preview = Preview();
    var currentImage = Path.Combine(root, "attachments", "articles", article.Id, stage.Id + ".png");
    Check(File.ReadAllBytes(currentImage).SequenceEqual(image), "all prior refusals retained current FAQ image");
    RestoreResult result;
    try { result = Restore(preview.ConfirmationToken) as RestoreResult ?? throw new Exception("Missing restore result."); }
    finally { db.BackupSourceVerifiedForTest = null; }
    Check(pendingOldLogin!.GetAwaiter().GetResult() is AppProblemException { Problem.Code: "AUTH-001" },
        "restore-first waiting login authenticates against the restored DB and rejects old credentials");
    Check(handleChecks == 2 && callbackCount == 1 && auth.GetCurrentUser() is null, "fresh confirmed restore succeeds once and logs out");
    Check(File.Exists(result.SafetyBackupPath) && result.Counts == preview.Counts && Hash(selected) == originalAHash, "success retains safety backup, selected archive, and inspected counts");
    auth.Login("legacy-admin", LegacyFixture.Password);
    Check(new ArticleViewService(db, auth, new ArticleAttachmentService(root)).GetArticle(LegacyFixture.ArticleId).Title == LegacyFixture.Title,
        "restored content is the inspected archive, not substituted content");
    Refused("successful token cannot be replayed after re-login", "BK-011", () => Restore(preview.ConfirmationToken));
    RehearsalDataRoot.ValidateInitialized(root);
    Check(db.QuickCheckForTest() == "ok", "persistent rehearsal remains structurally valid after confirmed restore");
    Console.WriteLine($"BackupConfirmationCheck PASS: {passed} checks; only generated OS-temporary files and synthetic DBs were used.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("BackupConfirmationCheck FAIL: " + exception.Message);
    return 1;
}
finally
{
    foreach (var root in roots)
        try { FileSystemBoundary.DeleteSyntheticRoot(root); }
        catch (Exception exception) { Console.Error.WriteLine("Synthetic cleanup incomplete: " + exception.GetType().Name); }
}

string NewRoot()
{
    var root = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
    if (File.Exists(root) || Directory.Exists(root)) throw new IOException("Synthetic root collision.");
    roots.Add(root); return root;
}
static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS: " + label); }
void Expect(string code, Action action, string label)
{
    try { action(); throw new InvalidOperationException("Expected " + code + ": " + label); }
    catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, label); }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan value) => _now += value;
}

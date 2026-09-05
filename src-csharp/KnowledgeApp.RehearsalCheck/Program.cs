using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;
using KnowledgeApp.Mail;
using Microsoft.Data.Sqlite;

var roots = new List<string>();
var passed = 0;
try
{
    Check(RehearsalDataRoot.FixedPath == Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RehearsalDataRoot.DirectoryName),
        "host root is the fixed isolated LocalApplicationData name (string comparison only)");
    Check(typeof(KnowledgeDatabase).GetMethods().Where(method => method.Name == "OpenRehearsal")
        .All(method => method.GetParameters().Length == 0), "public rehearsal entry point accepts no path or override");
    var root = NewRoot();
    var filesRoot = NewRoot();
    Directory.CreateDirectory(filesRoot);
    string articleId;
    string attachmentPath;
    string userId;
    byte[] image = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0];
    const string password = "Synthetic-Rehearsal\\*2026!";
    const string title = "合成永続確認：再起動後も画像は残りますか？";

    using (var db = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        Check(db.OpenInfo.Created && db.OpenInfo.PreviousSchemaVersion == 0, "first boot creates fresh current-schema DB");
        Check(db.SchemaVersionForTest() == MigrationCatalog.CurrentVersion && db.QuickCheckForTest() == "ok", "initial DB schema and quick_check");
        RehearsalDataRoot.ValidateInitialized(root);
        Check(File.Exists(Path.Combine(root, RehearsalDataRoot.InitializedMarker)) &&
            !File.Exists(Path.Combine(root, RehearsalDataRoot.InitializingMarker)), "initialization marker atomically completed");
        var auth = new AuthenticationService(db);
        Check(auth.GetCurrentUser() is null, "first session starts logged out");
        var admin = auth.Login("0000", "");
        var classification = new ClassificationSearchService(db, auth);
        var attachments = new ArticleAttachmentService(root);
        var editing = new ArticleEditingService(db, auth, attachments);
        Check(auth.ListUsers().Count == 1 && classification.ListCategories().Count == 0 &&
            editing.ListArticlesForManagement(new("", null, null, false, 1)).Total == 0,
            "only initial admin is seeded: no categories, FAQ, or sample users");
        userId = auth.CreateUser("rehearsal-user", "合成永続利用者", password, UserRoles.User).Id;
        auth.ResetUserPassword(admin.Id, password);
        auth.SavePasswordPolicy(new PasswordPolicySettings(false));
        new SettingsService(db, auth, root).SaveSettings(new AppSettings
            { ColorTheme = ColorThemes.Blue, ShowMascot = false, ShowTopCategoryInTitle = false });
        var category = classification.CreateCategory("合成永続分類", "再起動試験専用", null);
        editing.SaveTag(null, "合成永続タグ");
        var stage = attachments.StageBytes("合成永続画像.png", image);
        using var doc = JsonDocument.Parse($$$"""
            {"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"合成永続本文"}]},{"type":"image","attrs":{"attachmentId":"{{{stage.Id}}}","src":"knowledge-attachment:{{{stage.Id}}}","alt":"合成永続画像"}}]}
            """);
        var article = editing.SaveArticle(new SaveArticleInput(null, category.Id, title, "再起動後も保持します。", doc.RootElement,
            ArticleStatuses.Published, 2, null, null, false, [], [], [], [], [], [], ["合成永続タグ"], [], []));
        articleId = article.Id;
        attachmentPath = Path.Combine(root, "attachments", "articles", articleId, $"{stage.Id}.png");
        var searchId = classification.RecordSearchLog("合成永続", null, SearchScopes.All, 1);
        new ArticleViewService(db, auth, attachments).RecordArticleView(articleId, searchId);
        Check(File.ReadAllBytes(attachmentPath).SequenceEqual(image), "managed image committed through normal FAQ service");
    }

    Check(File.Exists(Path.Combine(root, "data", "knowledge.db")) && File.Exists(attachmentPath), "disposing DB preserves rehearsal DB and attachment");
    using (var db = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        Check(!db.OpenInfo.Created, "second boot reopens rather than reinitializes");
        var auth = new AuthenticationService(db);
        Check(auth.GetCurrentUser() is null, "second boot does not reuse login session");
        ExpectProblem(() => auth.Login("0000", ""), "AUTH-001", "previous empty password is not restored");
        auth.Login("0000", password);
        Check(auth.ListUsers().Count == 2 && auth.ListUsers().Any(user => user.Id == userId), "users and stable IDs survive reopen");
        Check(!auth.GetPasswordPolicy().AllowEmptyPasswords, "password policy survives reopen");
        var settings = new SettingsService(db, auth, root).GetSettings();
        Check(settings.ColorTheme == ColorThemes.Blue && !settings.ShowMascot && !settings.ShowTopCategoryInTitle, "appearance settings survive reopen");
        var attachments = new ArticleAttachmentService(root);
        var view = new ArticleViewService(db, auth, attachments);
        Check(view.GetArticle(articleId).Title == title && File.ReadAllBytes(attachmentPath).SequenceEqual(image), "FAQ and managed image survive reopen");
        var history = new HistoryService(db, auth);
        Check(history.ListSearchLogs(new("", null, null, false, 1)).Total == 1 &&
            history.ListViewLogs(new("", null, null, 1)).Total == 1, "search and view history survive reopen");
        var backup = new BackupService(db, auth, root);
        var target = Path.Combine(filesRoot, "Rehearsal_synthetic.faqbackup");
        RehearsalDataRoot.ValidateInitialized(root);
        backup.CreateFullBackup(new(target, "Rehearsal_synthetic", false));
        using (var zip = ZipFile.OpenRead(target))
        {
            Check(zip.GetEntry("manifest.json") is not null && zip.GetEntry("data/knowledge.db") is not null &&
                !zip.Entries.Any(entry => entry.FullName.Contains(".knowledgeapp-rehearsal", StringComparison.Ordinal)),
                "full backup format is unchanged and excludes lifecycle marker");
        }
        var before = SHA256.HashData(File.ReadAllBytes(target));
        new SettingsService(db, auth, root).SaveSettings(new AppSettings { ColorTheme = ColorThemes.Green });
        new ArticleEditingService(db, auth, attachments).DeleteArticle(articleId);
        var restored = backup.RestoreBackup(target);
        Check(auth.GetCurrentUser() is null && File.Exists(restored.SafetyBackupPath), "restore creates safety backup and logs out");
        Check(SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(before), "restore preserves source backup bytes");
        RehearsalDataRoot.ValidateInitialized(root);
        Check(Directory.Exists(Path.Combine(root, "restore-staging")), "persistent lifecycle accepts legitimate restore-staging parent");
    }

    using (var db = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        var auth = new AuthenticationService(db);
        Check(auth.GetCurrentUser() is null, "post-restore restart is logged out");
        auth.Login("0000", password);
        Check(new ArticleViewService(db, auth).GetArticle(articleId).DeletedAt is null &&
            new SettingsService(db, auth, root).GetSettings().ColorTheme == ColorThemes.Blue,
            "restored FAQ and settings persist through another restart");
        Check(File.ReadAllBytes(attachmentPath).SequenceEqual(image), "restored image survives another restart");
        var directBackup = Path.Combine(root, "Rehearsal_direct.faqbackup");
        new BackupService(db, auth, root).CreateFullBackup(new(directBackup, "Rehearsal_direct", false));
        RehearsalDataRoot.ValidateInitialized(root);
        Check(File.Exists(directBackup), "explicit backup directly in initialized root remains supported");
    }
    var dbHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "data", "knowledge.db")));
    var browser1 = TemporaryBrowserStorage.Create();
    var browser2 = TemporaryBrowserStorage.Create();
    try
    {
        Check(browser1.Root != browser2.Root && browser1.Root != root && browser2.Root != root, "browser profiles receive distinct disposable roots");
        Directory.CreateDirectory(browser1.ProfileDirectory);
        File.WriteAllText(Path.Combine(browser1.ProfileDirectory, "synthetic.txt"), "ephemeral", new UTF8Encoding(false));
        Check(browser1.TryCleanup() && browser1.TryCleanup(), "browser-only cleanup is safe and repeatable");
        Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "data", "knowledge.db"))).SequenceEqual(dbHash) &&
            File.ReadAllBytes(attachmentPath).SequenceEqual(image), "browser cleanup never changes persistent test DB or attachment");
    }
    finally { browser1.TryCleanup(); browser2.TryCleanup(); }

    using (var db = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        var auth = new AuthenticationService(db);
        var currentUser = auth.Login("0000", password);
        var backup = new BackupService(db, auth, root);
        var baselineBackup = Path.Combine(root, "Rehearsal_direct.faqbackup");
        var baselineHash = SHA256.HashData(File.ReadAllBytes(baselineBackup));
        foreach (var oldSchema in new[] { true, false })
        {
            // This fixture removes only the v7 migration receipt from a v7 DB. It is
            // deliberately forged, not a genuine historical v6 schema.
            var label = oldSchema ? "forged v6 backup with existing v7 columns" : "administrator-missing backup";
            var caseBackup = CreatePolicyBackupFixture(baselineBackup, filesRoot, oldSchema);
            var sourceHash = SHA256.HashData(File.ReadAllBytes(caseBackup));
            ExpectProblem(() => backup.InspectBackup(caseBackup), "BK-006", label + " is refused during staged preview validation");
            ExpectProblem(() => backup.RestoreBackup(caseBackup), "BK-006", label + " is refused before persistent restore swap");
            var article = new ArticleViewService(db, auth).GetArticle(articleId);
            var settings = new SettingsService(db, auth, root).GetSettings();
            Check(article.Title == title && article.DeletedAt is null && File.ReadAllBytes(attachmentPath).SequenceEqual(image) &&
                settings.ColorTheme == ColorThemes.Blue && !settings.ShowMascot && !settings.ShowTopCategoryInTitle &&
                db.SchemaVersionForTest() == MigrationCatalog.CurrentVersion,
                label + " rejection retains current FAQ, image, appearance, and schema");
            Check(auth.GetCurrentUser()?.Id == currentUser.Id && auth.ListUsers().Count == 2 &&
                !auth.GetPasswordPolicy().AllowEmptyPasswords,
                label + " rejection retains current session, users, and password policy");
            Check(SHA256.HashData(File.ReadAllBytes(caseBackup)).SequenceEqual(sourceHash) &&
                SHA256.HashData(File.ReadAllBytes(baselineBackup)).SequenceEqual(baselineHash),
                label + " rejection preserves selected and original backup bytes");
            RehearsalDataRoot.ValidateInitialized(root);
        }
    }
    using (var db = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        var auth = new AuthenticationService(db);
        auth.Login("0000", password);
        Check(new ArticleViewService(db, auth).GetArticle(articleId).Title == title &&
            File.ReadAllBytes(attachmentPath).SequenceEqual(image), "persistent state remains reopenable after both refused restores");
    }

    var providerCalls = 0;
    var writer = RehearsalMailDelegation.CreateWriter(() => { providerCalls++; return root; });
    var mail = new MailPreviewItem("synthetic-never-opened.msg", "synthetic.msg", "合成メール委譲",
        "合成送信者", "合成宛先", null, "外部送信しない合成本文") { IsSelected = true };
    var delegation = writer.WriteSelected([mail]);
    var delegationFile = Path.Combine(root, "codex-bridge", "mail-delegations", $"{delegation.DelegationId:D}.knowledge-mail-delegation.json");
    Check(providerCalls == 1 && File.Exists(delegationFile), "mail factory writes only under injected initialized rehearsal root");
    using (KnowledgeDatabase.OpenRehearsalForTest(root)) { }
    RehearsalDataRoot.ValidateInitialized(root);
    Check(File.Exists(Path.Combine(root, "Rehearsal_direct.faqbackup")) && File.Exists(delegationFile),
        "direct backup and explicit mail delegation survive initialized-root restart");
    Check(writer.Delete(delegation.DelegationId) && providerCalls == 2 && !File.Exists(delegationFile),
        "mail deletion revalidates root provider and deletes only selected delegation");
    var badMailRoot = Path.Combine(filesRoot, "not-allowed-mail-root");
    try
    {
        RehearsalMailDelegation.CreateWriter(() => badMailRoot).WriteSelected([mail]);
        throw new InvalidOperationException("Unsafe mail root accepted.");
    }
    catch (IOException) { Check(!Directory.Exists(badMailRoot), "mail factory rejects arbitrary root before any directory creation"); }

    foreach (var futureInWal in new[] { false, true })
    {
        var walSource = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(walSource)) { }
        var walTarget = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(walTarget)) { }
        var sourceDb = Path.Combine(walSource, "data", "knowledge.db");
        var targetDb = Path.Combine(walTarget, "data", "knowledge.db");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = sourceDb, Pooling = false }.ToString()))
        {
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" + (futureInWal
                ? "INSERT INTO schema_migrations(version, applied_at) VALUES (99, '2099-01-01')"
                : "UPDATE users SET display_name='合成WAL保持管理者'");
            command.ExecuteNonQuery();
            File.Copy(sourceDb, targetDb, overwrite: true);
            File.Copy(sourceDb + "-wal", targetDb + "-wal", overwrite: false);
        }
        Check(File.Exists(targetDb + "-wal") && !File.Exists(targetDb + "-shm"), "synthetic restart fixture retains committed WAL without SHM");
        if (futureInWal) ExpectRejectedUnchanged(walTarget, "future schema committed only in WAL is rejected without source mutation");
        else
        {
            using var recovered = KnowledgeDatabase.OpenRehearsalForTest(walTarget);
            var auth = new AuthenticationService(recovered);
            Check(auth.Login("0000", "").DisplayName == "合成WAL保持管理者", "preflight and reopen preserve committed WAL data");
        }
    }

    var unknown = NewRoot(); Directory.CreateDirectory(unknown);
    File.WriteAllText(Path.Combine(unknown, "unknown.txt"), "preserve", new UTF8Encoding(false));
    ExpectRejectedUnchanged(unknown, "unrecognized existing data is not adopted or reset");
    var pending = NewRoot(); Directory.CreateDirectory(pending);
    File.WriteAllBytes(Path.Combine(pending, RehearsalDataRoot.InitializingMarker), RehearsalDataRoot.MarkerContent.ToArray());
    ExpectRejectedUnchanged(pending, "incomplete initialization is never automatically retried or cleaned");
    var markerOnly = NewRoot(); Directory.CreateDirectory(markerOnly);
    File.WriteAllBytes(Path.Combine(markerOnly, RehearsalDataRoot.InitializedMarker), RehearsalDataRoot.MarkerContent.ToArray());
    ExpectRejectedUnchanged(markerOnly, "initialized root with missing DB is not recreated");
    var badMarker = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(badMarker)) { }
    File.WriteAllText(Path.Combine(badMarker, RehearsalDataRoot.InitializedMarker), "unknown-format", new UTF8Encoding(false));
    ExpectRejectedUnchanged(badMarker, "unknown marker content is rejected without mutation");
    var corrupt = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(corrupt)) { }
    File.WriteAllText(Path.Combine(corrupt, "data", "knowledge.db"), "synthetic corrupt DB", new UTF8Encoding(false));
    ExpectRejectedUnchanged(corrupt, "corrupt DB is rejected without write-open or reset");
    foreach (var version in new[] { 6, 99 })
    {
        var versionRoot = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(versionRoot)) { }
        ExecuteFixtureSql(versionRoot, version == 6 ? "DELETE FROM schema_migrations WHERE version = 7" :
            "INSERT INTO schema_migrations(version, applied_at) VALUES (99, '2099-01-01')");
        ExpectRejectedUnchanged(versionRoot, version == 6 ? "old DB is rejected without automatic migration" : "future DB is rejected without mutation");
    }
    var noUsers = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(noUsers)) { }
    ExecuteFixtureSql(noUsers, "DELETE FROM users");
    ExpectRejectedUnchanged(noUsers, "missing users never silently reseeds initial admin");
    var missingTable = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(missingTable)) { }
    ExecuteFixtureSql(missingTable, "DROP TABLE app_settings");
    ExpectRejectedUnchanged(missingTable, "incomplete current-version schema is rejected without mutation");
    var noAdmin = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(noAdmin)) { }
    ExecuteFixtureSql(noAdmin, "UPDATE users SET is_active = 0");
    ExpectRejectedUnchanged(noAdmin, "missing active administrator is refused without recovery bypass");
    var unknownAfter = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(unknownAfter)) { }
    var personal = Path.Combine(unknownAfter, "unrelated-personal-folder"); Directory.CreateDirectory(personal);
    File.WriteAllText(Path.Combine(unknownAfter, "unrelated.txt"), "preserve-root", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(personal, "unrelated.txt"), "preserve-nested", new UTF8Encoding(false));
    using (KnowledgeDatabase.OpenRehearsalForTest(unknownAfter)) { }
    Check(File.ReadAllText(Path.Combine(unknownAfter, "unrelated.txt"), Encoding.UTF8) == "preserve-root" &&
        File.ReadAllText(Path.Combine(personal, "unrelated.txt"), Encoding.UTF8) == "preserve-nested",
        "completed root preserves and ignores unknown personal files and folders");
    var knownFile = NewRoot(); using (KnowledgeDatabase.OpenRehearsalForTest(knownFile)) { }
    File.WriteAllText(Path.Combine(knownFile, "attachments"), "wrong-kind", new UTF8Encoding(false));
    ExpectRejectedUnchanged(knownFile, "file substituted for known managed directory is rejected");
    var arbitrary = Path.Combine(filesRoot, "not-an-allowed-data-root");
    ExpectProblem(() => KnowledgeDatabase.OpenRehearsalForTest(arbitrary), "DB-001", "internal test injection cannot open arbitrary nested directory");
    try { FileSystemBoundary.ValidateManagedDataRoot(arbitrary); throw new Exception("Invalid root accepted"); }
    catch (IOException) { Check(true, "services still reject arbitrary data roots"); }
    Check(FileSystemBoundary.ValidateSyntheticRoot(root) == root, "original strict synthetic boundary remains compatible");
    Console.WriteLine($"RehearsalCheck PASS: {passed} checks; real rehearsal root and production data were not opened.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("RehearsalCheck FAIL: " + exception.Message);
    return 1;
}
finally
{
    foreach (var root in roots) FileSystemBoundary.DeleteSyntheticRoot(root);
}

string NewRoot()
{
    var root = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
    if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Synthetic root collision.");
    roots.Add(root);
    return root;
}
void Check(bool condition, string title)
{
    if (!condition) throw new InvalidOperationException(title);
    passed++; Console.WriteLine($"PASS: {title}");
}
void ExpectProblem(Action action, string code, string title)
{
    try { action(); throw new InvalidOperationException($"Expected {code}: {title}"); }
    catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, title); }
}
void ExecuteFixtureSql(string root, string sql)
{
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "data", "knowledge.db"), Pooling = false }.ToString());
    db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
}
Dictionary<string, string> Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
void ExpectRejectedUnchanged(string root, string title)
{
    var before = Fingerprint(root);
    ExpectProblem(() => { using var rejected = KnowledgeDatabase.OpenRehearsalForTest(root); }, "DB-001", title);
    var after = Fingerprint(root);
    Check(before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var hash) && hash == item.Value), title + " / bytes preserved");
}
string CreatePolicyBackupFixture(string original, string filesRoot, bool oldSchema)
{
    var fixtureRoot = NewRoot();
    Directory.CreateDirectory(Path.Combine(fixtureRoot, "data"));
    var snapshot = Path.Combine(fixtureRoot, "data", "knowledge.db");
    var target = Path.Combine(filesRoot, oldSchema ? "Old_schema_synthetic.faqbackup" : "No_admin_synthetic.faqbackup");
    File.Copy(original, target, overwrite: false);
    using (var archive = ZipFile.OpenRead(target))
    using (var input = archive.GetEntry("data/knowledge.db")!.Open())
    using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
    ExecuteFixtureSql(fixtureRoot, oldSchema ? "DELETE FROM schema_migrations WHERE version = 7" :
        "UPDATE users SET is_active = 0 WHERE role = 'admin'");
    var bytes = File.ReadAllBytes(snapshot);
    using (var archive = ZipFile.Open(target, ZipArchiveMode.Update))
    {
        var oldManifest = archive.GetEntry("manifest.json")!;
        JsonObject manifest;
        using (var input = oldManifest.Open()) manifest = JsonNode.Parse(input)!.AsObject();
        var files = manifest["files"]!.AsArray();
        var databaseEntry = files.Single(file => file!["path"]!.GetValue<string>() == "data/knowledge.db")!;
        databaseEntry["size"] = bytes.LongLength;
        databaseEntry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        manifest["totalBytes"] = files.Sum(file => file!["size"]!.GetValue<long>());
        if (oldSchema) manifest["schemaVersion"] = 6;
        archive.GetEntry("data/knowledge.db")!.Delete();
        using (var output = archive.CreateEntry("data/knowledge.db", CompressionLevel.Optimal).Open()) output.Write(bytes);
        oldManifest.Delete();
        using var manifestOutput = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
        manifestOutput.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }
    return target;
}

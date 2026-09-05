using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Data;

var roots = new List<string>();
var retainedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var passed = 0;
try
{
    if (args.Length != 0)
    {
        if (args is not ["--write-manual-fixtures"]) throw new ArgumentException("Only --write-manual-fixtures is supported; no arbitrary path or real DB is accepted.");
        foreach (var version in new[] { 5, 6 })
        {
            var fixture = LegacyFixture.Create(NewRoot(), version);
            retainedRoots.Add(fixture.Root);
            Console.WriteLine($"SYNTHETIC_MANUAL_V{version}={fixture.Archive}");
            Console.WriteLine($"SHA256={Hash(fixture.Archive)}");
            Console.WriteLine(version == 5 ? "SYNTHETIC_LOGIN=0000 / password is empty" : "SYNTHETIC_LOGIN=legacy-admin / Synthetic-Legacy\\*2026!");
        }
        Console.WriteLine("Fixtures contain generated test data only. Never restore them into production. Root directories were retained in OS temporary storage for explicit copying of the two .faqbackup files.");
        return 0;
    }

    for (var version = 1; version <= MigrationCatalog.CurrentVersion; version++) RunValid(LegacyFixture.Create(NewRoot(), version));
    RunValid(LegacyFixture.Create(NewRoot(), 6, emptyV6: true));
    RunValid(LegacyFixture.Create(NewRoot(), 6, initialAdminWithNullAudits: true, caseLabel: "existing initial administrator fills only NULL audits"));
    var longText = new string('あ', 100_001);
    RunValid(LegacyFixture.Create(NewRoot(), 5, bodyOverride: DocumentWithImage(TextParagraph(longText)), plainOverride: longText, caseLabel: "100001-character body"));
    var manyParagraphs = string.Join(',', Enumerable.Repeat(TextParagraph("合成"), 10_001));
    RunValid(LegacyFixture.Create(NewRoot(), 5, bodyOverride: DocumentWithImage(manyParagraphs), plainOverride: string.Join('\n', Enumerable.Repeat("合成", 10_001)), caseLabel: "10001-paragraph body"));
    var deepParagraph = TextParagraph("合成深い本文");
    for (var depth = 0; depth < 24; depth++) deepParagraph = "{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[" + deepParagraph + "]}]}";
    RunValid(LegacyFixture.Create(NewRoot(), 5, bodyOverride: DocumentWithImage(deepParagraph), plainOverride: "合成深い本文", caseLabel: "JSON depth above64 below128"));

    var rejectionTarget = NewRoot();
    using (var db = KnowledgeDatabase.OpenRehearsalForTest(rejectionTarget))
    {
        var auth = new AuthenticationService(db); var admin = auth.Login("0000", "");
        var classification = new ClassificationSearchService(db, auth);
        var category = classification.CreateCategory("拒否時に保持する合成分類", "変更禁止", null);
        new SettingsService(db, auth, rejectionTarget).SaveSettings(new AppSettings { ColorTheme = ColorThemes.Blue });
        var attachments = new ArticleAttachmentService(rejectionTarget);
        var stage = attachments.StageBytes("拒否時に保持する合成画像.png", LegacyFixture.CreatePng());
        using var currentBody = JsonDocument.Parse($$$"""
            {"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"拒否時に保持する合成本文"}]},{"type":"image","attrs":{"attachmentId":"{{{stage.Id}}}","src":"knowledge-attachment:{{{stage.Id}}}","alt":"拒否時に保持する合成画像"}}]}
            """);
        var editing = new ArticleEditingService(db, auth, attachments);
        editing.SaveTag(null, "合成保持タグ");
        var currentArticle = editing.SaveArticle(new SaveArticleInput(null, category.Id,
            "拒否時に保持する合成FAQ", "画像・本文・履歴を保持します。", currentBody.RootElement,
            ArticleStatuses.Published, 2, null, null, false, [], [], [], [], [], [], ["合成保持タグ"], [], []));
        var searchId = classification.RecordSearchLog("合成保持", null, SearchScopes.All, 1);
        new ArticleViewService(db, auth, attachments).RecordArticleView(currentArticle.Id, searchId);
        var service = new BackupService(db, auth, rejectionTarget);
        foreach (var (label, fixture, expectedCode) in new (string, LegacyFixture, string)[]
        {
            ("future format", LegacyFixture.Create(NewRoot(), 7, manifestVersion: 99), "BK-009"),
            ("existing v6 users without active administrator", LegacyFixture.Create(NewRoot(), 6, mutation: "UPDATE users SET is_active=0 WHERE role='admin'"), "BK-006"),
            ("current v7 empty users", LegacyFixture.Create(NewRoot(), 7, emptyV6: true), "BK-006"),
            ("v6 empty users with non-null dangling audit", LegacyFixture.Create(NewRoot(), 6, emptyV6: true, mutation: "PRAGMA foreign_keys=OFF; UPDATE articles SET created_by_user_id='missing-synthetic-user'"), "BK-006"),
            ("v6 NULL audit without fixed initial administrator", LegacyFixture.Create(NewRoot(), 6, mutation: "UPDATE articles SET updated_by_user_id=NULL"), "BK-006"),
            ("v7 NULL audit even with fixed initial administrator", LegacyFixture.Create(NewRoot(), 7, initialAdminWithNullAudits: true), "BK-006"),
            ("migration statement failure", LegacyFixture.Create(NewRoot(), 6, mutation: "ALTER TABLE articles ADD COLUMN management_code TEXT"), "BK-006"),
            ("invalid rich text", LegacyFixture.Create(NewRoot(), 5, mutation: "UPDATE articles SET body_doc_json='{\"type\":\"script\"}'"), "BK-006"),
            ("unsafe link in otherwise valid paragraph", LegacyFixture.Create(NewRoot(), 5, bodyOverride: "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"合成危険リンク\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"javascript:alert(1)\"}}]}]}]}"), "BK-006"),
            ("image metadata hash mismatch", LegacyFixture.Create(NewRoot(), 5, mutation: "UPDATE article_attachments SET sha256='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'"), "BK-006"),
            ("invalid appearance setting", LegacyFixture.Create(NewRoot(), 5, mutation: "UPDATE app_settings SET value_json='{\"colorTheme\":\"unsafe\"}' WHERE key='appearance'"), "BK-006")
        }) RejectUnchanged(label, fixture.Archive, expectedCode, service, db, auth, admin.Id, rejectionTarget);

        var corrupt = LegacyFixture.Create(NewRoot(), 5);
        var corruptDbArchive = Path.Combine(corrupt.Root, "Synthetic_corrupt_database.faqbackup");
        using (var zip = ZipFile.OpenRead(corrupt.Archive))
        {
            var files = zip.Entries.Where(entry => entry.FullName != "manifest.json").ToDictionary(entry => entry.FullName, entry =>
            { using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray(); });
            files["data/knowledge.db"] = Encoding.UTF8.GetBytes("This is explicitly synthetic invalid SQLite data.");
            LegacyFixture.WriteArchive(corruptDbArchive, 5, files);
        }
        RejectUnchanged("corrupt DB with valid archive hashes", corruptDbArchive, "BK-006", service, db, auth, admin.Id, rejectionTarget);
        var malformedArchive = Path.Combine(corrupt.Root, "Synthetic_corrupt_archive.faqbackup");
        File.WriteAllText(malformedArchive, "not a ZIP; generated synthetic fixture", new UTF8Encoding(false));
        RejectUnchanged("corrupt archive", malformedArchive, "BK-006", service, db, auth, admin.Id, rejectionTarget);
    }
    using (var reopened = KnowledgeDatabase.OpenRehearsalForTest(rejectionTarget))
    {
        var auth = new AuthenticationService(reopened); auth.Login("0000", "");
        Check(new ClassificationSearchService(reopened, auth).ListCategories().Single().Name == "拒否時に保持する合成分類", "all refused restores leave rehearsal reopenable");
    }
    Console.WriteLine($"LegacyBackupCheck PASS: {passed} checks; only generated OS-temporary archives and isolated rehearsal roots were opened.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("LegacyBackupCheck FAIL: " + exception.Message);
    return 1;
}
finally
{
    foreach (var root in roots.Where(root => !retainedRoots.Contains(root)))
        try { FileSystemBoundary.DeleteSyntheticRoot(root); }
        catch (Exception exception) { Console.Error.WriteLine("Synthetic cleanup could not complete: " + exception.GetType().Name); }
}

string NewRoot()
{
    var root = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
    if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Synthetic root collision.");
    roots.Add(root); return root;
}
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    passed++; Console.WriteLine("PASS: " + label);
}
static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
static string TextParagraph(string text) => JsonSerializer.Serialize(new { type = "paragraph", content = new[] { new { type = "text", text } } });
static string DocumentWithImage(string nodes) => "{\"type\":\"doc\",\"content\":[" + nodes + "," +
    JsonSerializer.Serialize(new { type = "image", attrs = new { attachmentId = LegacyFixture.ImageId, src = "knowledge-attachment:" + LegacyFixture.ImageId, alt = "合成旧版の青緑チェック画像" } }) + "]}";
void ExpectProblem(Action action, string code, string label)
{
    try { action(); throw new InvalidOperationException("Expected " + code + ": " + label); }
    catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, label); }
}
void RunValid(LegacyFixture fixture)
{
    var label = $"v{fixture.Version}" + (fixture.Version == 6 && fixture.Bootstrap ? " uninitialized" : "") + (fixture.CaseLabel.Length > 0 ? " " + fixture.CaseLabel : "");
    var target = NewRoot(); var archiveHash = Hash(fixture.Archive); string markerHash;
    using (var db = KnowledgeDatabase.OpenRehearsalForTest(target))
    {
        var auth = new AuthenticationService(db); auth.Login("0000", "");
        var currentCategory = new ClassificationSearchService(db, auth).CreateCategory("復元前の合成分類", "安全バックアップで保持", null);
        markerHash = Hash(Path.Combine(target, RehearsalDataRoot.InitializedMarker));
        var service = new BackupService(db, auth, target);
        var preview = service.InspectBackup(fixture.Archive);
        Check(preview.SchemaVersion == fixture.Version && preview.Counts == new BackupCounts(2, 2, 1, 1), label + " preview retains original version and counts excluding deleted FAQ");
        Check(Hash(fixture.Archive) == archiveHash && new ClassificationSearchService(db, auth).ListCategories().Single().Id == currentCategory.Id, label + " preview preserves original archive and current DB");
        var restored = service.RestoreBackup(fixture.Archive);
        Check(auth.GetCurrentUser() is null && File.Exists(restored.SafetyBackupPath), label + " success logs out and retains pre-restore safety backup");
        using (var zip = ZipFile.OpenRead(restored.SafetyBackupPath))
        {
            var safetyRoot = NewRoot(); Directory.CreateDirectory(safetyRoot); var snapshotPath = Path.Combine(safetyRoot, "snapshot.db");
            using (var source = zip.GetEntry("data/knowledge.db")!.Open()) using (var output = File.Create(snapshotPath)) source.CopyTo(output);
            using var safety = LegacyFixture.Open(snapshotPath);
            Check(LegacyFixture.Scalar(safety, "SELECT name FROM categories") == "復元前の合成分類", label + " safety archive holds the actual prior state");
        }
        Check(Hash(fixture.Archive) == archiveHash && Hash(Path.Combine(target, RehearsalDataRoot.InitializedMarker)) == markerHash, label + " restore never edits selected archive or lifecycle marker");
        Check(db.SchemaVersionForTest() == 7 && db.QuickCheckForTest() == "ok", label + " restored staged DB is schema7 and passes quick_check");
        using (var connection = LegacyFixture.Open(db.OpenInfo.DatabasePath))
        {
            var shape = fixture.Before.ToDictionary(item => item.Key, item => item.Value);
            if (fixture.Version == 6 && fixture.Bootstrap)
            {
                shape.Remove("users");
                var columns = shape["articles"].Columns.Where(column => column is not ("created_by_user_id" or "updated_by_user_id")).ToArray();
                using var source = LegacyFixture.Open(fixture.DatabasePath);
                shape["articles"] = LegacyFixture.Snapshot(source, new() { ["articles"] = new(columns, []) })["articles"];
            }
            if (fixture.RepairAudits)
            {
                var old = shape["articles"];
                shape["articles"] = old with { Rows = old.Rows.Select(row => JsonSerializer.Serialize(
                    JsonSerializer.Deserialize<JsonElement[]>(row)!.Select((value, index) =>
                        value.ValueKind == JsonValueKind.Null && old.Columns[index] is "created_by_user_id" or "updated_by_user_id"
                            ? (object)KnowledgeDatabase.InitialAdminUserId : value).ToArray())).Order(StringComparer.Ordinal).ToArray() };
            }
            var actual = LegacyFixture.Snapshot(connection, shape);
            foreach (var table in shape) Check(table.Value.Rows.SequenceEqual(actual[table.Key].Rows), label + " preserves original columns/rows: " + table.Key);
            var expectedAdmin = fixture.Bootstrap ? KnowledgeDatabase.InitialAdminUserId : fixture.ExistingAdminId;
            Check(LegacyFixture.Scalar(connection, "SELECT COUNT(*) FROM users") == (fixture.Bootstrap ? "1" : "2"), label + " seeds only genuine pre-auth archives; existing users are preserved");
            Check(LegacyFixture.Scalar(connection, "SELECT created_by_user_id FROM articles WHERE id='" + LegacyFixture.ArticleId + "'") == expectedAdmin &&
                  LegacyFixture.Scalar(connection, "SELECT updated_by_user_id FROM articles WHERE id='" + LegacyFixture.ArticleId + "'") == (fixture.Bootstrap ? expectedAdmin : LegacyFixture.UserId), label + " FAQ audit attribution is correct");
            Check(LegacyFixture.Scalar(connection, "SELECT group_concat(management_code,',') FROM (SELECT management_code FROM articles ORDER BY created_at,id)") == "FAQ-00001,FAQ-00002,FAQ-00003" &&
                  LegacyFixture.Scalar(connection, "SELECT next_value FROM management_code_sequences WHERE entity_type='article'") == "4", label + " management codes include deleted FAQ and sequence advances");
            Check(LegacyFixture.Scalar(connection, "SELECT group_concat(management_code,',') FROM (SELECT management_code FROM categories ORDER BY created_at,id)") == "CAT-00001,CAT-00002", label + " category management codes retain deterministic order");
        }
        auth.Login(fixture.Bootstrap ? "0000" : fixture.AdminLogin, fixture.Bootstrap ? "" : LegacyFixture.Password);
        var attachments = new ArticleAttachmentService(target);
        var article = new ArticleViewService(db, auth, attachments).GetArticle(LegacyFixture.ArticleId);
        Check(article.Title == LegacyFixture.Title && article.BodyDoc.GetRawText() == fixture.ExpectedBody && article.Tags.SequenceEqual(new[] { "合成タグ" }) && article.Attachments.Count == 1, label + " normal FAQ API renders original body, tag and image metadata");
        var view = new ArticleViewService(db, auth, attachments);
        Check(view.GetArticle(LegacyFixture.RelatedId).Id == LegacyFixture.RelatedId && view.GetArticle(LegacyFixture.DeletedId).DeletedAt == LegacyFixture.Timestamp,
            label + " related/merged and logically deleted FAQ also remain readable by administrator");
        Check(File.ReadAllBytes(Path.Combine(target, LegacyFixture.ImagePath.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(fixture.Image), label + " managed PNG bytes retained");
        Check(File.ReadAllText(Path.Combine(target, "settings", "synthetic-legacy.json"), Encoding.UTF8) == "{\"synthetic\":true}" &&
            File.ReadAllText(Path.Combine(target, "manuals", "synthetic-manual", "readme.txt"), Encoding.UTF8).StartsWith("旧版互換確認用", StringComparison.Ordinal), label + " legacy managed files preserved without opening external references");
        var settings = new SettingsService(db, auth, target).GetSettings();
        Check(settings.ColorTheme == ColorThemes.Blue && !settings.ShowMascot && !settings.ShowTopCategoryInTitle, label + " appearance settings are visible through normal service");
        var classification = new ClassificationSearchService(db, auth);
        foreach (var term in LegacyFixture.SearchTerms.Append("合成別称"))
            Check(classification.SearchArticles(new(term, LegacyFixture.CategoryId, SearchScopes.Descendants, false, 1, SearchSorts.UpdatedDesc)).Items.Any(item => item.Id == LegacyFixture.ArticleId), label + " legacy indexed field/synonym searchable: " + term);
        Check(db.FtsRowCountForTest() == 3 && db.FtsCountForArticleForTest(LegacyFixture.ArticleId) == 1, label + " preserved FTS contains no duplicate rows");
        var history = new HistoryService(db, auth);
        Check(history.ListSearchLogs(new("", null, null, false, 1)).Total == 1 && history.ListViewLogs(new("", null, null, 1)).Total == 1, label + " original search and view history remain accessible");
        var newCategory = classification.CreateCategory("移行後の採番確認", "合成", null);
        using var afterInsert = LegacyFixture.Open(db.OpenInfo.DatabasePath);
        Check(LegacyFixture.Scalar(afterInsert, "SELECT management_code FROM categories WHERE id='" + newCategory.Id + "'") == "CAT-00003", label + " migrated allocation trigger works for next category");
    }
    using (var db = KnowledgeDatabase.OpenRehearsalForTest(target))
    {
        var auth = new AuthenticationService(db);
        Check(auth.GetCurrentUser() is null, label + " restart creates a new logged-out session");
        auth.Login(fixture.Bootstrap ? "0000" : fixture.AdminLogin, fixture.Bootstrap ? "" : LegacyFixture.Password);
        Check(new ArticleViewService(db, auth).GetArticle(LegacyFixture.ArticleId).Title == LegacyFixture.Title && Hash(Path.Combine(target, RehearsalDataRoot.InitializedMarker)) == markerHash, label + " migrated rehearsal reopens without reseed or marker replacement");
        if (!fixture.Bootstrap)
        {
            auth.Logout(); Check(auth.Login("legacy-user", LegacyFixture.Password).Id == LegacyFixture.UserId, label + " special-character password and existing nonadmin ID survive restore/restart");
            ExpectProblem(() => new BackupService(db, auth, target).InspectBackup(fixture.Archive), "AUTH-003", label + " preserved user role cannot access administrator backup operation");
        }
    }
}
void RejectUnchanged(string label, string archive, string code, BackupService service, KnowledgeDatabase db, AuthenticationService auth, string userId, string target)
{
    var sourceHash = Hash(archive); var marker = Hash(Path.Combine(target, RehearsalDataRoot.InitializedMarker));
    using var beforeConnection = LegacyFixture.Open(db.OpenInfo.DatabasePath);
    var before = LegacyFixture.Snapshot(beforeConnection);
    var fileState = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToDictionary(path => path, Hash);
    ExpectProblem(() => service.InspectBackup(archive), code, label + " refused during preview");
    ExpectProblem(() => service.RestoreBackup(archive), code, label + " refused before restore swap");
    using var afterConnection = LegacyFixture.Open(db.OpenInfo.DatabasePath);
    var after = LegacyFixture.Snapshot(afterConnection, before);
    Check(before.All(table => table.Value.Rows.SequenceEqual(after[table.Key].Rows)), label + " refusal preserves every current DB table");
    Check(fileState.All(file => File.Exists(file.Key) && Hash(file.Key) == file.Value) && Hash(Path.Combine(target, RehearsalDataRoot.InitializedMarker)) == marker, label + " refusal preserves current managed files and marker");
    Check(Hash(archive) == sourceHash && auth.GetCurrentUser()?.Id == userId, label + " refusal preserves original archive and current login");
    Check(!Directory.Exists(Path.Combine(target, "safety-backups")) || Directory.GetFiles(Path.Combine(target, "safety-backups")).Length == 0, label + " refused preview does not create misleading safety backups");
}

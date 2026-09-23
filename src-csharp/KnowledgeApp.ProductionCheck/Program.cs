using System.Text.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

Console.OutputEncoding = new UTF8Encoding(false);
try { return ProductionCheck.Run(args); }
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }

internal static class ProductionCheck
{
    private static readonly List<string> Roots = [];
    private static int _passed;

    internal static int Run(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("Only fresh synthetic roots are accepted.");
        try
        {
            CheckFresh();
            CheckSeparatedStores();
            CheckLegacyShapedStore();
            CheckCompetingSqlite();
            CheckBackupFailure();
            CheckInvalidStores();
            CheckLinks();
            Console.WriteLine($"ProductionCheck: {_passed} synthetic checks passed; productionDataOpened=false.");
            return 0;
        }
        finally
        {
            foreach (var root in Roots) FileSystemBoundary.DeleteSyntheticRoot(root);
        }
    }

    private static void CheckFresh()
    {
        var root = NewRoot();
        string firstBackup;
        using (var database = KnowledgeDatabase.OpenProductionForTest(root))
        {
            Check(database.OpenInfo.Created, "fresh candidate initializes once");
            firstBackup = database.CandidateSafetyBackup!.DestinationPath;
            Check(File.Exists(firstBackup), "candidate baseline backup verified before use");
            Check(File.Exists(Path.Combine(root, ProductionDataRoot.InitializedMarker)) &&
                !File.Exists(Path.Combine(root, ProductionDataRoot.InitializingMarker)), "new initialization marker commits once");
            var auth = new AuthenticationService(database);
            Check(auth.GetCurrentUser() is null && auth.Login("0000", "").Role == "admin", "fresh candidate starts logged out and authenticates normally");
            ExpectLocked(root, "other SQLite runtime cannot read or write while candidate holds exclusive lease");
        }
        var firstHash = Hash(firstBackup);
        using (var database = KnowledgeDatabase.OpenProductionForTest(root))
        {
            Check(!database.OpenInfo.Created, "restart preserves candidate store");
            Check(database.CandidateSafetyBackup!.DestinationPath != firstBackup && Hash(firstBackup) == firstHash,
                "each candidate launch creates a distinct baseline without replacing the prior archive");
            Check(new AuthenticationService(database).GetCurrentUser() is null, "restart never restores authentication session");
        }
        using var connection = LegacyFixture.Open(DatabasePath(root), readOnly: false);
        Check(LegacyFixture.Scalar(connection, "SELECT count(*) FROM users") == "1", "legacy SQLite runtime can reopen after candidate closes");
    }

    private static void CheckLegacyShapedStore()
    {
        var fixture = LegacyShapedStore();
        var root = fixture.Root;
        var expected = Snapshot(root);
        var files = ManagedFileHashes(root);
        var sourceHash = Hash(fixture.Archive);
        string baseline;
        using (var database = KnowledgeDatabase.OpenProductionForTest(root))
        {
            Check(!database.OpenInfo.Created && database.OpenInfo.PreviousSchemaVersion == 7 && database.OpenInfo.CurrentSchemaVersion == MigrationCatalog.CurrentVersion && database.OpenInfo.MigrationBackupPath is not null, "existing schema 7 migrates after a full safety backup without bootstrap");
            Check(!File.Exists(Path.Combine(root, ProductionDataRoot.InitializedMarker)), "legacy store does not require or silently acquire a C# marker");
            baseline = database.CandidateSafetyBackup!.DestinationPath;
            Check(database.CandidateSafetyBackup.Counts == new BackupCounts(2, 2, 1, 1), "pre-authentication baseline protects FAQ, categories, image and legacy file");
            using var zip = ZipFile.OpenRead(baseline);
            Check(zip.GetEntry(LegacyFixture.ImagePath) is not null && zip.GetEntry("settings/synthetic-legacy.json") is not null &&
                !zip.Entries.Any(entry => entry.FullName.Contains("codex-", StringComparison.Ordinal) || entry.FullName.Contains(".knowledgeapp", StringComparison.Ordinal)),
                "baseline format retains managed files and excludes bridge files and lifecycle markers");
        }
        // Compare original columns; schema 9 intentionally adds revision, owners and personal settings.
        using (var migratedConnection = LegacyFixture.Open(DatabasePath(root)))
        {
            expected.Remove("schema_migrations"); expected.Remove("app_settings");
            var oldUsers = expected["users"];
            expected["users"] = oldUsers with { Rows = oldUsers.Rows.Select(row => JsonSerializer.Serialize(
                JsonSerializer.Deserialize<JsonElement[]>(row)!.Select((value, index) =>
                    oldUsers.Columns[index] == "role" && value.GetString() == "user" ? (object)"editor" : value).ToArray())).Order(StringComparer.Ordinal).ToArray() };
            var migrated = LegacyFixture.Snapshot(migratedConnection, expected);
            Check(expected.All(table => table.Value.Rows.SequenceEqual(migrated[table.Key].Rows)),
                "schema 7 migration preserves original IDs, hashes, audits and history; user becomes editor");
        }
        CheckFiles(root, files, "candidate startup preserves every existing managed file and unknown file");
        Check(Hash(fixture.Archive) == sourceHash, "pre-existing source backup is unchanged");
        using (var database = KnowledgeDatabase.OpenProductionForTest(root))
        {
            var auth = new AuthenticationService(database);
            ExpectProblem(() => auth.Login(fixture.AdminLogin, ""), "AUTH-001", "existing nonempty password is not reset during adoption");
            Check(auth.Login(fixture.AdminLogin, LegacyFixture.Password).Id == fixture.ExistingAdminId && auth.ListUsers().Count == 2,
                "existing users, special-character password and roles work without new admin creation");
            var view = new ArticleViewService(database, auth, new ArticleAttachmentService(root));
            Check(view.GetArticle(LegacyFixture.ArticleId).Title == LegacyFixture.Title, "existing FAQ is readable through authenticated service");
            new SettingsService(database, auth, root).SaveSettings(new AppSettings { ColorTheme = ColorThemes.Green });
            new ArticleEditingService(database, auth, new ArticleAttachmentService(root)).DeleteArticle(LegacyFixture.RelatedId);
            var backup = new BackupService(database, auth, root);
            var baselineHash = Hash(baseline);
            var restored = backup.RestoreBackup(baseline);
            Check(auth.GetCurrentUser() is null && File.Exists(restored.SafetyBackupPath), "restore uses normal safety backup and invalidates login");
            Check(Hash(baseline) == baselineHash, "candidate rollback never modifies selected baseline backup");
            ExpectLocked(root, "successful restore keeps the same exclusive SQLite lease until app disposal");
            auth.Login(fixture.AdminLogin, LegacyFixture.Password);
            Check(new ArticleViewService(database, auth).GetArticle(LegacyFixture.RelatedId).DeletedAt is null &&
                new SettingsService(database, auth, root).GetSettings().ColorTheme == ColorThemes.Blue,
                "restored FAQ and appearance are available through existing services");
        }
        CheckFiles(root, files, "restore retains images, legacy settings, unselected bridge files and unknown files");
        using var reopened = KnowledgeDatabase.OpenProductionForTest(root);
        var reopenedAuth = new AuthenticationService(reopened);
        reopenedAuth.Login(fixture.AdminLogin, LegacyFixture.Password);
        Check(new ArticleViewService(reopened, reopenedAuth).GetArticle(LegacyFixture.RelatedId).DeletedAt is null,
            "post-restore candidate restart retains restored FAQ");
    }

    private static void CheckSeparatedStores()
    {
        Check(ProductionDataRoot.DirectoryName == "jp.local.webknowledgesystem.csharp" &&
            !string.Equals(ProductionDataRoot.DirectoryName, RehearsalDataRoot.DirectoryName, StringComparison.OrdinalIgnoreCase),
            "C# production selects only its dedicated fixed namespace, not Tauri or rehearsal");
        var legacy = LegacyShapedStore();
        var sourceBefore = Fingerprint(legacy.Root);
        var target = NewRoot();
        using (var database = KnowledgeDatabase.OpenProductionForTest(target))
        {
            var auth = new AuthenticationService(database);
            auth.Login("0000", "");
            var backup = new BackupService(database, auth, target);
            Check(backup.GetOverview().Counts.Articles == 0,
                "a fresh C# store remains empty even when a separate legacy store has FAQs");
            Check(database.StartupSafetyBackup is not null && File.Exists(database.StartupSafetyBackup.DestinationPath),
                "the empty C# store gets its own startup safety archive");
            using (var oldRuntime = LegacyFixture.Open(legacy.DatabasePath))
                Check(LegacyFixture.Scalar(oldRuntime, "SELECT count(*) FROM articles WHERE deleted_at IS NULL") == "2",
                    "C# exclusive ownership does not open or lock the separate legacy DB");

            var result = backup.RestoreBackup(legacy.Archive);
            Check(auth.GetCurrentUser() is null && File.Exists(result.SafetyBackupPath),
                "only explicit selected full-backup restore transfers data and logs out");
            auth.Login(legacy.AdminLogin, LegacyFixture.Password);
            var view = new ArticleViewService(database, auth, new ArticleAttachmentService(target));
            Check(view.GetArticle(LegacyFixture.ArticleId).Title == LegacyFixture.Title &&
                backup.GetOverview().Counts == new BackupCounts(2, 2, 1, 1),
                "selected backup transfers existing IDs, users, FAQs and managed image to C#");
            new ArticleEditingService(database, auth, new ArticleAttachmentService(target)).DeleteArticle(LegacyFixture.RelatedId);
            new SettingsService(database, auth, target).SaveSettings(new AppSettings { ColorTheme = ColorThemes.Green });
            CheckFingerprints(sourceBefore, Fingerprint(legacy.Root),
                "C# restore and later edits do not synchronize, delete or modify any legacy source file");
        }
        using (var reopened = KnowledgeDatabase.OpenProductionForTest(target))
        {
            var auth = new AuthenticationService(reopened);
            auth.Login(legacy.AdminLogin, LegacyFixture.Password);
            Check(new ArticleViewService(reopened, auth).GetArticle(LegacyFixture.RelatedId).DeletedAt is not null &&
                new SettingsService(reopened, auth, target).GetSettings().ColorTheme == ColorThemes.Green,
                "C# restart retains its own post-cutover edits without reimporting old data");
        }
        CheckFingerprints(sourceBefore, Fingerprint(legacy.Root),
            "the complete separate legacy store and selected source backup remain byte-for-byte unchanged");
    }

    private static void CheckCompetingSqlite()
    {
        foreach (var readOnly in new[] { true, false })
        {
            var fixture = LegacyShapedStore();
            var expected = Snapshot(fixture.Root);
            using (var competing = LegacyFixture.Open(fixture.DatabasePath, readOnly))
            {
                LegacyFixture.Execute(competing, readOnly ? "BEGIN; SELECT count(*) FROM articles" : "BEGIN IMMEDIATE");
                var before = Fingerprint(fixture.Root);
                var started = Stopwatch.StartNew();
                ExpectRejected(() => { using var refused = KnowledgeDatabase.OpenProductionForTest(fixture.Root); },
                    "existing " + (readOnly ? "reader" : "writer") + " prevents candidate startup");
                Check(started.Elapsed < TimeSpan.FromSeconds(15), "competing SQLite rejection respects bounded lock-wait");
                CheckFingerprints(before, Fingerprint(fixture.Root), "occupied-source refusal never mutates source bytes or creates a baseline");
                LegacyFixture.Execute(competing, "ROLLBACK");
            }
            CheckSnapshots(expected, Snapshot(fixture.Root), "competing-runtime rejection retains all data");
            using var database = KnowledgeDatabase.OpenProductionForTest(fixture.Root);
            Check(database.CandidateSafetyBackup is not null, "candidate can start normally after competing runtime closes");
        }
    }

    private static void CheckBackupFailure()
    {
        var fixture = LegacyShapedStore();
        var expected = Snapshot(fixture.Root);
        var image = Path.Combine(fixture.Root, LegacyFixture.ImagePath);
        var files = ManagedFileHashes(fixture.Root);
        using (var unavailable = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ExpectProblem(() => { using var refused = KnowledgeDatabase.OpenProductionForTest(fixture.Root); }, "BK-003",
                "unreadable managed image makes automatic startup baseline fail and stops candidate use");
        }
        CheckSnapshots(expected, Snapshot(fixture.Root), "baseline failure occurs before login, catalog updates or logical DB changes");
        CheckFiles(fixture.Root, files, "baseline failure retains existing managed files");
        Check(!Directory.Exists(Path.Combine(fixture.Root, "safety-backups")) ||
            Directory.GetFiles(Path.Combine(fixture.Root, "safety-backups"), "*.faqbackup").Length == 0,
            "failed baseline does not publish an incomplete archive");
        using var database = KnowledgeDatabase.OpenProductionForTest(fixture.Root);
        Check(database.CandidateSafetyBackup is not null, "startup baseline can be retried after only the external file lock is released");
    }

    private static void CheckInvalidStores()
    {
        foreach (var variant in new[] { "missing-db", "zero-db", "corrupt-db", "future-schema", "missing-table", "no-users", "inactive-admin", "bad-marker", "unfinished-marker", "known-directory-file" })
        {
            var fixture = LegacyShapedStore();
            switch (variant)
            {
                case "missing-db": File.Move(fixture.DatabasePath, fixture.DatabasePath + ".synthetic-retained"); break;
                case "zero-db": File.WriteAllBytes(fixture.DatabasePath, []); break;
                case "corrupt-db": File.WriteAllText(fixture.DatabasePath, "synthetic corrupt SQLite", new UTF8Encoding(false)); break;
                case "bad-marker": Write(fixture.Root, ProductionDataRoot.InitializedMarker, "synthetic invalid marker"); break;
                case "unfinished-marker": Write(fixture.Root, ProductionDataRoot.InitializingMarker, "synthetic pending initialization"); break;
                case "known-directory-file": Write(fixture.Root, "temp", "synthetic wrong entry type"); break;
                default:
                    using (var database = LegacyFixture.Open(fixture.DatabasePath, readOnly: false))
                    {
                        LegacyFixture.Execute(database, variant switch
                        {
                            "future-schema" => "INSERT INTO schema_migrations(version,applied_at) VALUES (99,'2099-01-01')",
                            "missing-table" => "DROP TABLE app_settings",
                            "no-users" => "PRAGMA foreign_keys=OFF; DELETE FROM users",
                            "inactive-admin" => "UPDATE users SET is_active=0 WHERE role='admin'",
                            _ => throw new Exception("Unknown synthetic case")
                        });
                    }
                    break;
            }
            var before = Fingerprint(fixture.Root);
            ExpectRejected(() => { using var rejected = KnowledgeDatabase.OpenProductionForTest(fixture.Root); }, variant + " is refused before use");
            CheckFingerprints(before, Fingerprint(fixture.Root), variant + " is never reset, repaired or deleted silently");
        }
        foreach (var version in Enumerable.Range(1, 6))
        {
            var fixture = LegacyFixture.Create(NewRoot(), version);
            var before = Fingerprint(fixture.Root);
            ExpectRejected(() => { using var rejected = KnowledgeDatabase.OpenProductionForTest(fixture.Root); }, "direct old schema " + version + " still requires explicit backup import");
            CheckFingerprints(before, Fingerprint(fixture.Root), "rejected old schema " + version + " bytes are unchanged");
        }
        var unknownRoot = NewRoot();
        Write(unknownRoot, "unknown.txt", "synthetic unrelated file");
        var unknownBefore = Fingerprint(unknownRoot);
        ExpectRejected(() => { using var rejected = KnowledgeDatabase.OpenProductionForTest(unknownRoot); }, "nonempty unknown root is not adopted");
        CheckFingerprints(unknownBefore, Fingerprint(unknownRoot), "unknown nonempty root remains untouched");
        var gitRoot = LegacyShapedStore().Root;
        Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
        var gitBefore = Fingerprint(gitRoot);
        ExpectRejected(() => { using var rejected = KnowledgeDatabase.OpenProductionForTest(gitRoot); }, "production initializer rejects synthetic Git root");
        CheckFingerprints(gitBefore, Fingerprint(gitRoot), "Git-root rejection has no writes");
    }

    private static void CheckLinks()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows junction checks must run on Windows.");
        foreach (var relative in new[] { "data", ProductionDataRoot.InitializedMarker, "data/knowledge.db-wal", "attachments/articles/linked" })
        {
            var fixture = LegacyShapedStore();
            var outside = NewRoot();
            Write(outside, "sentinel.txt", "synthetic outside target must be retained");
            var before = Fingerprint(outside);
            var link = Path.Combine(fixture.Root, relative);
            if (Directory.Exists(link)) Directory.Move(link, link + "-synthetic-retained");
            CreateJunction(fixture.Root, link, outside);
            try
            {
                ExpectRejected(() => { using var rejected = KnowledgeDatabase.OpenProductionForTest(fixture.Root); }, relative + " junction is rejected");
                CheckFingerprints(before, Fingerprint(outside), relative + " cannot write or erase through the link");
            }
            finally
            {
                FileSystemBoundary.ValidateManagedPath(fixture.Root, Path.GetDirectoryName(link)!);
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new IOException("Synthetic junction unexpectedly changed.");
                Directory.Delete(link, recursive: false);
            }
        }
    }

    private static LegacyFixture LegacyShapedStore()
    {
        var fixture = LegacyFixture.Create(NewRoot(), 7);
        using var archive = ZipFile.OpenRead(fixture.Archive);
        foreach (var relative in new[] { LegacyFixture.ImagePath, "manuals/synthetic-manual/readme.txt", "settings/synthetic-legacy.json" })
        {
            var path = FileSystemBoundary.ValidateManagedPath(fixture.Root, Path.Combine(fixture.Root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = archive.GetEntry(relative)!.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        Write(fixture.Root, "codex-inbox/synthetic-unselected.txt", "unselected synthetic bridge file");
        Write(fixture.Root, "unknown-folder/synthetic.txt", "unrelated synthetic file");
        return fixture;
    }

    private static void CreateJunction(string root, string link, string target)
    {
        FileSystemBoundary.ValidateManagedPath(root, link);
        FileSystemBoundary.ValidateSyntheticRoot(target);
        if (File.Exists(link) || Directory.Exists(link)) throw new IOException("Synthetic link already exists.");
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void ExpectLocked(string root, string label)
    {
        try
        {
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath(root), Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1
            }.ToString());
            database.Open();
            LegacyFixture.Scalar(database, "SELECT count(*) FROM articles");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { Check(true, label); return; }
        throw new Exception("Exclusive database lease was lost: " + label);
    }

    private static string NewRoot()
    {
        var root = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}"));
        if (File.Exists(root) || Directory.Exists(root)) throw new IOException("Synthetic root collision.");
        Roots.Add(root); return root;
    }
    private static string DatabasePath(string root) => Path.Combine(root, "data", "knowledge.db");
    private static void Write(string root, string relative, string text)
    {
        var path = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
    private static Dictionary<string, TableSnapshot> Snapshot(string root) { using var database = LegacyFixture.Open(DatabasePath(root)); return LegacyFixture.Snapshot(database); }
    private static void CheckSnapshots(Dictionary<string, TableSnapshot> before, Dictionary<string, TableSnapshot> after, string label) =>
        Check(before.Count == after.Count && before.All(table => after.TryGetValue(table.Key, out var current) &&
            table.Value.Columns.SequenceEqual(current.Columns) && table.Value.Rows.SequenceEqual(current.Rows)), label);
    private static string Hash(string path) { using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private static Dictionary<string, string> Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), Hash, StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> ManagedFileHashes(string root) => Fingerprint(root)
        .Where(item => !item.Key.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !item.Key.StartsWith("safety-backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToDictionary();
    private static void CheckFiles(string root, Dictionary<string, string> files, string label) => Check(files.All(item =>
        File.Exists(Path.Combine(root, item.Key)) && Hash(Path.Combine(root, item.Key)) == item.Value), label);
    private static void CheckFingerprints(Dictionary<string, string> before, Dictionary<string, string> after, string label) =>
        Check(before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var hash) && hash == item.Value), label);
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); _passed++; Console.WriteLine("PASS: " + label); }
    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (Exception exception) when (exception is AppProblemException or IOException or UnauthorizedAccessException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void ExpectProblem(Action action, string code, string label)
    {
        try { action(); }
        catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, label); return; }
        throw new Exception("Expected " + code + ": " + label);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int bytesReturned, IntPtr overlapped);
}

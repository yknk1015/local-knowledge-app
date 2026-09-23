using System.IO.Compression;
using System.Text.Json;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

internal static class RecoveryKeyCheck
{
    internal static void Run()
    {
        var root = NewRoot();
        var migrationRoot = NewRoot();
        try
        {
            CheckLifecycle(root);
            CheckMigration(migrationRoot);
            Console.WriteLine("PASS: recovery lifecycle, authorization, persisted cooldown, expiry, revocation, atomic failure, backup restore and schema 7 migration (synthetic data only)");
        }
        finally
        {
            FileSystemBoundary.DeleteSyntheticRoot(root);
            FileSystemBoundary.DeleteSyntheticRoot(migrationRoot);
        }
    }

    private static void CheckLifecycle(string root)
    {
        var clock = new ManualClock();
        string key;
        using (var db = KnowledgeDatabase.OpenSynthetic(root))
        {
            var auth = new AuthenticationService(db, clock);
            Expect("AUTH-002", () => auth.IssueRecoveryKey(new("")));
            Expect("REC-002", () => auth.VerifyRecoveryKey(new("0000", "not-a-key")));
            auth.Login("0000", "");
            Check(auth.GetRecoveryKeyStatus() is { HasKey: false, NeedsSetup: true }, "new administrator needs setup");
            Check(!auth.SkipRecoverySetup().NeedsSetup, "setup may be skipped");
            Expect("AUTH-001", () => auth.IssueRecoveryKey(new("incorrect")));
            key = auth.IssueRecoveryKey(new("")).Key;
            Check(key.Length == 19 && key.Split('-').All(group => group.Length == 4), "short grouped key");
            Check(RecoveryKeyCodec.Normalize(key)?.Length == 16 && RecoveryKeyCodec.Normalize("０" + key) is null,
                "normalization accepts only the defined alphabet");
            using (var connection = Open(root))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key_hash FROM user_recovery_keys WHERE user_id = $id";
                command.Parameters.AddWithValue("$id", KnowledgeDatabase.InitialAdminUserId);
                var hash = (string)command.ExecuteScalar()!;
                Check(hash != key && Argon2PasswordCodec.IsSupportedHash(hash) &&
                    Argon2PasswordCodec.Verify(RecoveryKeyCodec.Normalize(key)!, hash), "only a supported salted hash is persisted");
            }
            var ordinary = auth.CreateUser("ordinary", "合成一般利用者", "Synthetic-user!", UserRoles.User);
            var second = auth.CreateUser("second", "合成管理者", "Synthetic-admin!", UserRoles.Admin);
            auth.Logout();
            auth.Login("ordinary", "Synthetic-user!");
            Expect("AUTH-003", () => auth.GetRecoveryKeyStatus());
            Expect("AUTH-003", () => auth.IssueRecoveryKey(new("Synthetic-user!")));
            auth.Logout();
            Expect("REC-001", () => auth.VerifyRecoveryKey(new("ordinary", key)));
            Expect("REC-001", () => auth.VerifyRecoveryKey(new("missing", key)));
            for (var attempt = 0; attempt < 4; attempt++)
                Expect("REC-001", () => auth.VerifyRecoveryKey(new("0000", "invalid")));
            Expect("REC-003", () => auth.VerifyRecoveryKey(new("0000", "invalid")));
            Expect("REC-003", () => auth.VerifyRecoveryKey(new("0000", key)));
        }
        using (var db = KnowledgeDatabase.OpenSynthetic(root))
        {
            var auth = new AuthenticationService(db, clock);
            Expect("REC-003", () => auth.VerifyRecoveryKey(new("0000", key)));
            clock.Advance(TimeSpan.FromMinutes(15));
            var grant = auth.VerifyRecoveryKey(new("００００", key.ToLowerInvariant().Replace('-', ' ')));
            var restarted = new AuthenticationService(db, clock);
            Expect("REC-004", () => restarted.CompletePasswordRecovery(new(grant.Token, "new", "new")));
            Check(auth.GetCurrentUser() is null, "recovery proof never creates a login session");
            Expect("AUTH-002", () => auth.ListUsers());
            Expect("REC-005", () => auth.CompletePasswordRecovery(new(grant.Token, "", "")));
            Expect("REC-005", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "different")));
            clock.Advance(TimeSpan.FromMinutes(5));
            Expect("REC-004", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "new")));
            grant = auth.VerifyRecoveryKey(new("0000", key));
            auth.CancelPasswordRecovery();
            Expect("REC-004", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "new")));

            grant = auth.VerifyRecoveryKey(new("0000", key));
            db.RecoverySavingForTest = () => throw new InvalidOperationException("Synthetic transaction failure");
            try { auth.CompletePasswordRecovery(new(grant.Token, "Synthetic-new!", "Synthetic-new!")); throw new Exception("failure hook was missed"); }
            catch (InvalidOperationException exception) when (exception.Message == "Synthetic transaction failure") { }
            finally { db.RecoverySavingForTest = null; }
            Check(db.Authenticate("0000", "").Role == UserRoles.Admin, "failed reset retains original password");
            var otherSession = new AuthenticationService(db);
            otherSession.Login("0000", "");
            var competing = new AuthenticationService(db, clock);
            var competingGrant = competing.VerifyRecoveryKey(new("0000", key));
            var replacement = auth.CompletePasswordRecovery(new(grant.Token, "Synthetic-new!", "Synthetic-new!"));
            Check(replacement.Key != key && otherSession.GetCurrentUser() is null, "successful reset rotates key and invalidates other session");
            Expect("REC-004", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "new")));
            Expect("REC-004", () => competing.CompletePasswordRecovery(new(competingGrant.Token, "new", "new")));
            Expect("AUTH-001", () => auth.Login("0000", ""));
            Expect("REC-001", () => auth.VerifyRecoveryKey(new("0000", key)));
            Check(auth.GetCurrentUser() is null, "completion requires a new normal login");
            auth.Login("0000", "Synthetic-new!");
            Expect("REC-001", () => auth.VerifyRecoveryKey(new("0000", replacement.Key)));
            var reissued = auth.IssueRecoveryKey(new("Synthetic-new!")).Key;
            auth.Logout();
            Expect("REC-001", () => auth.VerifyRecoveryKey(new("0000", replacement.Key)));
            grant = auth.VerifyRecoveryKey(new("0000", reissued));
            var admin = new AuthenticationService(db);
            admin.Login("second", "Synthetic-admin!");
            admin.ResetUserPassword(KnowledgeDatabase.InitialAdminUserId, "Synthetic-changed!");
            Expect("REC-004", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "new")));
            Expect("REC-002", () => auth.VerifyRecoveryKey(new("0000", reissued)));

            auth.Login("0000", "Synthetic-changed!");
            key = auth.IssueRecoveryKey(new("Synthetic-changed!")).Key;
            var backup = new BackupService(db, auth, root);
            var archive = backup.CreateFullBackup(new(Path.Combine(root, "recovery.faqbackup"), "合成復旧", false));
            auth.Logout();
            grant = auth.VerifyRecoveryKey(new("0000", key));
            admin.SetUserActive(KnowledgeDatabase.InitialAdminUserId, false);
            Expect("REC-001", () => competing.VerifyRecoveryKey(new("0000", key)));
            Expect("REC-004", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "new")));
            admin.SetUserActive(KnowledgeDatabase.InitialAdminUserId, true);
            Expect("REC-002", () => auth.VerifyRecoveryKey(new("0000", key)));

            var adminBackup = new BackupService(db, admin, root);
            adminBackup.RestoreBackup(archive.DestinationPath);
            grant = auth.VerifyRecoveryKey(new("0000", key));
            admin.Login("second", "Synthetic-admin!");
            adminBackup.RestoreBackup(archive.DestinationPath);
            Expect("REC-004", () => auth.CompletePasswordRecovery(new(grant.Token, "new", "new")));
            auth.VerifyRecoveryKey(new("0000", key));
            Check(auth.GetCurrentUser() is null, "restored key requires fresh verification");
        }
    }

    private static void CheckMigration(string root)
    {
        using (var db = KnowledgeDatabase.OpenRehearsalForTest(root)) { }
        using (var connection = Open(root))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                DROP TABLE codex_proposal_owners;
                DROP TRIGGER articles_revision;
                ALTER TABLE articles DROP COLUMN revision;
                DROP INDEX ix_search_logs_user; DROP INDEX ix_view_logs_user;
                ALTER TABLE search_logs DROP COLUMN user_id; ALTER TABLE view_logs DROP COLUMN user_id;
                DROP TRIGGER users_create_recovery; DROP TRIGGER users_invalidate_recovery;
                DROP TABLE user_recovery_keys;
                CREATE TABLE legacy_users (
                    id TEXT PRIMARY KEY, login_id TEXT NOT NULL CHECK (length(trim(login_id)) BETWEEN 1 AND 100),
                    normalized_login_id TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 100),
                    password_hash TEXT NOT NULL, role TEXT NOT NULL CHECK (role IN ('admin', 'user')),
                    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, last_login_at TEXT);
                INSERT INTO legacy_users SELECT * FROM users;
                DROP TABLE users; ALTER TABLE legacy_users RENAME TO users;
                CREATE INDEX ix_users_active_role ON users(is_active, role);
                DELETE FROM schema_migrations WHERE version >= 8;
                PRAGMA foreign_keys = ON;
                """;
            command.ExecuteNonQuery();
        }
        string migrationBackup;
        using (var db = KnowledgeDatabase.OpenRehearsalForTest(root))
        {
            Check(db.OpenInfo.PreviousSchemaVersion == 7 && db.OpenInfo.CurrentSchemaVersion == MigrationCatalog.CurrentVersion, "persistent schema 7 migrates to 8");
            migrationBackup = db.OpenInfo.MigrationBackupPath ?? throw new Exception("migration backup missing");
            using var archive = ZipFile.OpenRead(migrationBackup);
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            Check(manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 7, "premigration backup has original schema");
            var auth = new AuthenticationService(db);
            auth.Login("0000", "");
            Check(auth.GetRecoveryKeyStatus() is { HasKey: false, NeedsSetup: false }, "existing users keep password and unissued key without forced setup");
            var backups = new BackupService(db, auth, root);
            Check(backups.InspectBackup(migrationBackup).SchemaVersion == 7, "premigration archive can be inspected");
            backups.RestoreBackup(migrationBackup);
            auth.Login("0000", "");
            Check(!auth.GetRecoveryKeyStatus().HasKey, "premigration archive restores through migration");
        }
        using (var connection = Open(root))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER users_invalidate_recovery; CREATE TRIGGER users_invalidate_recovery AFTER UPDATE ON users BEGIN SELECT 1; END;";
            command.ExecuteNonQuery();
        }
        Expect("DB-001", () => { using var invalid = KnowledgeDatabase.OpenRehearsalForTest(root); });
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan duration) { _now += duration; _ticks += duration.Ticks; }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
    private static SqliteConnection Open(string root)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "data", "knowledge.db"), Pooling = false }.ToString());
        db.Open();
        return db;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (AppProblemException exception) when (exception.Problem.Code == code) { return; }
        throw new InvalidOperationException($"Expected {code}");
    }
}

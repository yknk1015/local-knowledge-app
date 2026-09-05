using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase : IDisposable
{
    internal const string InitialAdminUserId = "00000000-0000-7000-8000-000000000000";
    private const string InitialAdminEmergencyPasswordHash = "$argon2id$v=19$m=19456,t=2,p=1$7SMc0zDN7fMmSfQcwgksbA$UB2H8zEmUGOAzJ7IuOCUwcpv4cs+UisQu9eXoryc61c";
    private const string PasswordPolicyKey = "password_policy";
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SqliteConnection _connection;
    private readonly object _sync = new();
    private readonly bool _persistentRehearsal;
    private bool _disposed;

    private KnowledgeDatabase(SqliteConnection connection, SyntheticDatabaseOpenInfo openInfo, bool persistentRehearsal)
    {
        _connection = connection;
        OpenInfo = openInfo;
        _persistentRehearsal = persistentRehearsal;
    }

    public SyntheticDatabaseOpenInfo OpenInfo { get; }

    public static KnowledgeDatabase OpenSynthetic(string dataRoot)
    {
        var root = ValidateSyntheticRoot(dataRoot);
        return OpenManaged(root, persistent: false, initializing: false);
    }

    public static KnowledgeDatabase OpenRehearsal()
    {
        try { return OpenRehearsalAt(RehearsalDataRoot.ValidateFixedPath(RehearsalDataRoot.FixedPath)); }
        catch (AppProblemException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw RehearsalDataRoot.Problem(); }
    }

    // This internal seam is only visible to explicitly named test assemblies. It
    // cannot redirect the host to arbitrary production or caller-selected data.
    internal static KnowledgeDatabase OpenRehearsalForTest(string syntheticRoot) =>
        OpenRehearsalAt(ValidateSyntheticRoot(syntheticRoot));

    private static KnowledgeDatabase OpenRehearsalAt(string root)
    {
        KnowledgeDatabase? database = null;
        try
        {
            var recovered = RecoverInterruptedRestore(root);
            var initializing = RehearsalDataRoot.Prepare(root);
            database = OpenManaged(root, persistent: true, initializing);
            database.RecoveredInterruptedRestore |= recovered;
            if (initializing) RehearsalDataRoot.CompleteInitialization(root);
            return database;
        }
        catch (AppProblemException) { database?.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            database?.Dispose();
            throw RehearsalDataRoot.Problem();
        }
    }

    private static KnowledgeDatabase OpenManaged(string root, bool persistent, bool initializing, bool exclusive = false)
    {
        var recovered = RecoverInterruptedRestore(root);
        var dataDirectory = Path.Combine(root, "data");
        var backupDirectory = Path.Combine(root, "safety-backups");
        if (!persistent || initializing)
        {
            FileSystemBoundary.CreateManagedDirectory(root, dataDirectory);
            FileSystemBoundary.CreateManagedDirectory(root, backupDirectory);
        }
        var databasePath = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(dataDirectory, "knowledge.db"));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            FileSystemBoundary.ValidateManagedPath(root, databasePath + suffix);
        var existed = File.Exists(databasePath);

        if (persistent && !initializing)
        {
            ValidatePersistentDatabaseBeforeOpen(root, databasePath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = persistent && !initializing ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());

        try
        {
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;");
            if (exclusive) AcquireExclusiveDatabase(connection);

            var previousVersion = existed ? ValidateExisting(connection) : 0;
            if (persistent && !initializing) RequireCurrentRehearsalVersion(previousVersion);
            string? backupPath = null;
            if (previousVersion is > 0 and < MigrationCatalog.CurrentVersion)
            {
                backupPath = CreateMigrationBackup(connection, backupDirectory, previousVersion);
            }

            if (previousVersion == 0)
            {
                ApplyMigration(connection, 1);
                previousVersion = 0;
            }
            ApplyPendingMigrations(connection);
            if (!persistent || initializing) EnsureInitialAdmin(connection);
            if (persistent) ValidatePersistentUsers(connection);
            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
            QuickCheck(connection);

            var currentVersion = SchemaVersion(connection);
            var info = new SyntheticDatabaseOpenInfo(
                databasePath,
                previousVersion,
                currentVersion,
                !existed,
                backupPath);
            return new KnowledgeDatabase(connection, info, persistent) { RecoveredInterruptedRestore = recovered };
        }
        catch (AppProblemException)
        {
            connection.Dispose();
            throw;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            throw new AppProblemException(AppProblem.Database("データベースの初期設定に失敗しました。"));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _connection.Dispose();
        }
    }

    internal AuthenticatedUser Authenticate(string loginId, string password)
    {
        if (CountRunes(loginId) > 100 || CountRunes(password) > 1024)
        {
            throw new AppProblemException(AppProblem.Authentication());
        }

        return ExecuteLocked(() =>
        {
            var normalized = NormalizeLoginId(loginId);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, login_id, display_name, password_hash, role, is_active
                  FROM users
                 WHERE normalized_login_id = $normalized_login_id
                """;
            command.Parameters.AddWithValue("$normalized_login_id", normalized);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(5) != 1)
            {
                throw new AppProblemException(AppProblem.Authentication());
            }

            var id = reader.GetString(0);
            var storedHash = reader.GetString(3);
            var role = ValidateRole(reader.GetString(4));
            if (!Argon2PasswordCodec.Verify(password, storedHash) &&
                !(id == InitialAdminUserId && Argon2PasswordCodec.Verify(password, InitialAdminEmergencyPasswordHash)))
            {
                throw new AppProblemException(AppProblem.Authentication());
            }

            var user = new AuthenticatedUser(id, reader.GetString(1), reader.GetString(2), role);
            reader.Close();
            using var update = _connection.CreateCommand();
            update.CommandText = "UPDATE users SET last_login_at = $last_login_at WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$last_login_at", UtcNow());
            update.ExecuteNonQuery();
            return user;
        });
    }

    internal IReadOnlyList<UserSummary> ListUsers() => ExecuteLocked(() =>
    {
        var users = new List<UserSummary>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, login_id, display_name, role, is_active, created_at, updated_at, last_login_at
              FROM users
             ORDER BY created_at, login_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            users.Add(ReadUser(reader));
        }
        return users;
    });

    internal UserSummary CreateUser(string loginId, string displayName, string password, string role)
    {
        EnsurePasswordAllowed(password);
        var validatedLoginId = ValidateUserText(loginId, "ログインID");
        var validatedDisplayName = ValidateUserText(displayName, "表示名");
        var validatedRole = ValidateRole(role);
        var normalized = NormalizeLoginId(validatedLoginId);
        return ExecuteLocked(() =>
        {
            using (var duplicate = _connection.CreateCommand())
            {
                duplicate.CommandText = "SELECT EXISTS(SELECT 1 FROM users WHERE normalized_login_id = $normalized_login_id)";
                duplicate.Parameters.AddWithValue("$normalized_login_id", normalized);
                if (Convert.ToInt64(duplicate.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                {
                    throw UserInputProblem("同じログインIDがすでに登録されています。", "別のログインIDを入力してください。");
                }
            }

            var id = Guid.CreateVersion7().ToString();
            var now = UtcNow();
            var passwordHash = Argon2PasswordCodec.Hash(password);
            using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO users(
                    id, login_id, normalized_login_id, display_name, password_hash,
                    role, is_active, created_at, updated_at
                ) VALUES (
                    $id, $login_id, $normalized_login_id, $display_name, $password_hash,
                    $role, 1, $created_at, $updated_at
                )
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$login_id", validatedLoginId);
            insert.Parameters.AddWithValue("$normalized_login_id", normalized);
            insert.Parameters.AddWithValue("$display_name", validatedDisplayName);
            insert.Parameters.AddWithValue("$password_hash", passwordHash);
            insert.Parameters.AddWithValue("$role", validatedRole);
            insert.Parameters.AddWithValue("$created_at", now);
            insert.Parameters.AddWithValue("$updated_at", now);
            insert.ExecuteNonQuery();
            return new UserSummary(id, validatedLoginId, validatedDisplayName, validatedRole, true, now, now, null);
        });
    }

    internal UserSummary SetUserActive(string id, bool isActive) => ExecuteLocked(() =>
    {
        if (!isActive)
        {
            using var lastAdmin = _connection.CreateCommand();
            lastAdmin.CommandText = """
                SELECT role = 'admin' AND (
                    SELECT COUNT(*) FROM users WHERE role = 'admin' AND is_active = 1
                ) <= 1
                  FROM users
                 WHERE id = $id
                """;
            lastAdmin.Parameters.AddWithValue("$id", id);
            var result = lastAdmin.ExecuteScalar();
            if (result is null)
            {
                throw UserNotFound();
            }
            if (Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1)
            {
                throw new AppProblemException(new AppProblem(
                    "USR-002",
                    "最後の有効な管理者は利用停止にできません。",
                    "別の管理者を追加してから利用停止にしてください。"));
            }
        }

        using var update = _connection.CreateCommand();
        update.CommandText = "UPDATE users SET is_active = $is_active, updated_at = $updated_at WHERE id = $id";
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$is_active", isActive ? 1 : 0);
        update.Parameters.AddWithValue("$updated_at", UtcNow());
        if (update.ExecuteNonQuery() == 0)
        {
            throw UserNotFound();
        }
        return GetUserUnlocked(id);
    });

    internal UserSummary ResetUserPassword(string id, string password)
    {
        EnsurePasswordAllowed(password);
        return ExecuteLocked(() =>
        {
            var passwordHash = Argon2PasswordCodec.Hash(password);
            using var update = _connection.CreateCommand();
            update.CommandText = "UPDATE users SET password_hash = $password_hash, updated_at = $updated_at WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$password_hash", passwordHash);
            update.Parameters.AddWithValue("$updated_at", UtcNow());
            if (update.ExecuteNonQuery() == 0)
            {
                throw UserNotFound();
            }
            return GetUserUnlocked(id);
        });
    }

    internal PasswordPolicySettings GetPasswordPolicy() => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", PasswordPolicyKey);
        var stored = command.ExecuteScalar() as string;
        if (stored is null)
        {
            return PasswordPolicySettings.Default;
        }
        try
        {
            return JsonSerializer.Deserialize<PasswordPolicySettings>(stored, SettingsJsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AppProblemException(new AppProblem(
                "SET-003",
                "パスワード設定を読み込めませんでした。",
                "設定画面で空パスワードの許可設定を選び直して保存してください。"));
        }
    });

    internal PasswordPolicySettings SavePasswordPolicy(PasswordPolicySettings settings) => ExecuteLocked(() =>
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, SettingsJsonOptions);
        }
        catch (JsonException)
        {
            throw new AppProblemException(new AppProblem(
                "SET-004",
                "パスワード設定を保存できませんでした。",
                "設定内容を確認して、もう一度保存してください。"));
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value_json, updated_at)
            VALUES ($key, $value_json, $updated_at)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$key", PasswordPolicyKey);
        command.Parameters.AddWithValue("$value_json", json);
        command.Parameters.AddWithValue("$updated_at", UtcNow());
        command.ExecuteNonQuery();
        return settings;
    });

    internal int SchemaVersionForTest() => ExecuteLocked(() => SchemaVersion(_connection));

    internal string QuickCheckForTest() => ExecuteLocked(() => QuickCheck(_connection));

    internal string PasswordHashForTest(string userId) => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", userId);
        return command.ExecuteScalar() as string ?? throw UserNotFound();
    });

    private void EnsurePasswordAllowed(string password)
    {
        Argon2PasswordCodec.ValidateLength(password);
        if (password.Length == 0 && !GetPasswordPolicy().AllowEmptyPasswords)
        {
            throw new AppProblemException(new AppProblem(
                "USR-004",
                "空欄のパスワードは現在許可されていません。",
                "1文字以上のパスワードを入力してください。既存利用者のパスワードは変更されません。"));
        }
    }

    private T ExecuteLocked<T>(Func<T> action)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfRestoreRecoveryRequired();
            try
            {
                return action();
            }
            catch (AppProblemException)
            {
                throw;
            }
            catch (SqliteException)
            {
                throw new AppProblemException(AppProblem.Database("データベースの処理に失敗しました。"));
            }
        }
    }

    private void ExecuteLocked(Action action) => ExecuteLocked(() =>
    {
        action();
        return true;
    });

    private UserSummary GetUserUnlocked(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, login_id, display_name, role, is_active, created_at, updated_at, last_login_at
              FROM users
             WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw UserNotFound();
        }
        return ReadUser(reader);
    }

    private static UserSummary ReadUser(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ValidateRole(reader.GetString(3)),
        reader.GetInt64(4) == 1,
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static int ValidateExisting(SqliteConnection connection)
    {
        QuickCheck(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM sqlite_master
             WHERE type = 'table'
               AND name IN ('schema_migrations', 'categories', 'articles')
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 3)
        {
            throw new AppProblemException(AppProblem.Database("FAQデータベースの形式を確認できないため、更新を中止しました。"));
        }

        var version = SchemaVersion(connection);
        if (version is < 1 or > MigrationCatalog.CurrentVersion)
        {
            var message = version > MigrationCatalog.CurrentVersion
                ? "このFAQデータは、現在のアプリより新しい形式です。アプリを更新してください。"
                : "FAQデータベースの版情報を確認できないため、更新を中止しました。";
            throw new AppProblemException(AppProblem.Database(message));
        }
        return version;
    }

    private static void ApplyPendingMigrations(SqliteConnection connection)
    {
        var version = SchemaVersion(connection);
        if (version > MigrationCatalog.CurrentVersion)
        {
            throw new AppProblemException(AppProblem.Database("このFAQデータは、現在のアプリより新しい形式です。アプリを更新してください。"));
        }
        for (var next = version + 1; next <= MigrationCatalog.CurrentVersion; next++)
        {
            ApplyMigration(connection, next);
        }
    }

    private static void ApplyMigration(SqliteConnection connection, int version)
    {
        try
        {
            ExecuteNonQuery(connection, MigrationCatalog.Load(version));
        }
        catch (SqliteException)
        {
            throw new AppProblemException(AppProblem.Database("FAQデータの更新に失敗しました。"));
        }
    }

    private static void EnsureInitialAdmin(SqliteConnection connection)
    {
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM users";
        var userCount = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (userCount == 0)
        {
            var now = UtcNow();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO users(
                    id, login_id, normalized_login_id, display_name, password_hash,
                    role, is_active, created_at, updated_at
                ) VALUES (
                    $id, '0000', '0000', '初期管理者', $password_hash,
                    'admin', 1, $created_at, $updated_at
                )
                """;
            insert.Parameters.AddWithValue("$id", InitialAdminUserId);
            insert.Parameters.AddWithValue("$password_hash", Argon2PasswordCodec.Hash(string.Empty));
            insert.Parameters.AddWithValue("$created_at", now);
            insert.Parameters.AddWithValue("$updated_at", now);
            insert.ExecuteNonQuery();
        }

        using var audit = connection.CreateCommand();
        audit.CommandText = """
            UPDATE articles
               SET created_by_user_id = COALESCE(created_by_user_id, $user_id),
                   updated_by_user_id = COALESCE(updated_by_user_id, $user_id)
            """;
        audit.Parameters.AddWithValue("$user_id", InitialAdminUserId);
        audit.ExecuteNonQuery();
    }

    private static void ValidatePersistentUsers(SqliteConnection connection)
    {
        // Reject incomplete schemas bearing a current version number instead of
        // opening a partly usable UI and discovering missing tables during edits.
        string[] requiredTables = ["schema_migrations", "categories", "articles", "users", "tags", "article_tags",
            "article_symptoms", "article_causes", "article_targets", "article_error_codes", "article_search_terms",
            "article_relations", "article_attachments", "manuals", "article_manual_links", "synonym_groups", "synonyms",
            "article_search_documents", "article_search_fts", "search_logs", "view_logs", "app_settings",
            "codex_proposal_receipts", "codex_proposal_history", "article_merge_relations", "management_code_sequences"];
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var schemas = schema.ExecuteReader();
            var present = new HashSet<string>(StringComparer.Ordinal);
            while (schemas.Read()) present.Add(schemas.GetString(0));
            if (requiredTables.Any(name => !present.Contains(name))) throw RehearsalDataRoot.Problem();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'admin' AND is_active = 1";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) throw RehearsalDataRoot.Problem();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        using var reader = foreignKeys.ExecuteReader();
        if (reader.Read()) throw RehearsalDataRoot.Problem();
    }

    private static void RequireCurrentRehearsalVersion(int version)
    {
        if (version != MigrationCatalog.CurrentVersion)
            throw new AppProblemException(AppProblem.Database(
                "この段階では旧版データの移行は行いません。データを削除せず、次の移行版で確認してください。"));
    }

    private static void ValidatePersistentDatabaseBeforeOpen(string root, string databasePath)
    {
        // SQLite can create WAL/SHM even for a read-only connection. Inspect an
        // isolated copy of the DB plus existing WAL so rejected stores are never
        // touched by SQLite itself. Source handles reject concurrent writers.
        var temporary = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(
            Path.GetTempPath(), $"knowledgeapp-csharp-auth-{Guid.NewGuid():D}"));
        if (Directory.Exists(temporary) || File.Exists(temporary)) throw RehearsalDataRoot.Problem();
        try
        {
            Directory.CreateDirectory(temporary);
            var walPath = FileSystemBoundary.ValidateManagedPath(root, databasePath + "-wal");
            using var source = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var wal = File.Exists(walPath)
                ? new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var snapshot = FileSystemBoundary.ValidateManagedPath(temporary, Path.Combine(temporary, "knowledge.db"));
            using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (wal is not null)
            {
                using var output = new FileStream(snapshot + "-wal", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                wal.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            using var preflight = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString());
            preflight.Open();
            var version = ValidateExisting(preflight);
            RequireCurrentRehearsalVersion(version);
            ValidatePersistentUsers(preflight);
        }
        catch (SqliteException) { throw RehearsalDataRoot.Problem(); }
        finally
        {
            // Only the fresh validation directory belongs to this operation. No
            // persistent root or existing sidecar is ever a cleanup target.
            FileSystemBoundary.DeleteSyntheticRoot(temporary);
        }
    }

    private static string CreateMigrationBackup(SqliteConnection source, string backupDirectory, int version)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var destinationPath = Path.Combine(
            backupDirectory,
            $"KnowledgeApp_CSharp_synthetic_before_v{version}_to_v{MigrationCatalog.CurrentVersion}_{timestamp}.faqbackup");
        FileSystemBoundary.ValidatePath(destinationPath, allowUnc: false);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            FileSystemBoundary.ValidatePath(destinationPath + suffix, allowUnc: false);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var destination = new SqliteConnection(builder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        QuickCheck(destination);
        if (SchemaVersion(destination) != version)
        {
            throw new AppProblemException(AppProblem.Database("DB移行前の安全バックアップを検証できないため、更新を中止しました。"));
        }
        return destinationPath;
    }

    private static int SchemaVersion(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations')";
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string QuickCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        var result = command.ExecuteScalar() as string;
        if (result != "ok")
        {
            throw new AppProblemException(AppProblem.Database("FAQデータベースが破損しているため、処理を中止しました。"));
        }
        return result;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ValidateSyntheticRoot(string dataRoot)
    {
        try
        {
            return FileSystemBoundary.ValidateSyntheticRoot(dataRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AppProblemException(AppProblem.Database("C#移行試験ではリンクを経由しないOS一時フォルダ直下の合成DBだけを使用できます。"));
        }
    }

    internal static string NormalizeLoginId(string value) =>
        value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private static string ValidateUserText(string value, string label)
    {
        var trimmed = value.Trim();
        if (CountRunes(trimmed) is < 1 or > 100)
        {
            throw UserInputProblem($"{label}は1～100文字で入力してください。", "入力内容を確認してください。");
        }
        return trimmed;
    }

    private static string ValidateRole(string role)
    {
        if (!UserRoles.IsValid(role))
        {
            throw new AppProblemException(AppProblem.Database("利用者の権限データが正しくありません。"));
        }
        return role;
    }

    private static AppProblemException UserInputProblem(string message, string action) =>
        new(new AppProblem("USR-001", message, action));

    private static AppProblemException UserNotFound() => UserInputProblem(
        "指定した利用者が見つかりません。",
        "利用者一覧を更新して、もう一度選択してください。");

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static int CountRunes(string value) => value.EnumerateRunes().Count();
}

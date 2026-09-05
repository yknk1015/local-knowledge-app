using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private static readonly ConcurrentDictionary<int, IReadOnlyList<BackupTableShape>> BackupSchemaShapes = new();

    private static void PrepareBackupSnapshotForRestore(string snapshot, TransferBackupManifest manifest)
    {
        // The archive has already been verified. Only its generated, extracted DB
        // is writable here; the original archive and current DB remain untouched.
        ValidateBackupSnapshot(snapshot, manifest.SchemaVersion);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshot,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            connection.Open();
            ExecuteNonQuery(connection,
                "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = DELETE;");
            ValidateBackupMigrationHistory(connection, manifest.SchemaVersion);
            ValidateRequiredBackupSchema(connection, manifest.SchemaVersion);
            var bootstrapLegacyAuthentication = RequiresLegacyBackupBootstrap(connection, manifest.SchemaVersion);
            ApplyPendingMigrations(connection);
            if (bootstrapLegacyAuthentication) EnsureInitialAdmin(connection);

            RequireCurrentRehearsalVersion(ValidateExisting(connection));
            ValidateRequiredBackupSchema(connection, MigrationCatalog.CurrentVersion);
            ValidatePersistentUsers(connection);
            ValidateBackupUsers(connection);
            ValidateBackupCategoryHierarchy(connection);
            ValidateBackupArticleContent(connection, manifest);
            ValidateBackupSettings(connection);
            QuickCheck(connection);
        }
        catch (AppProblemException exception) when (exception.Problem.Code != "BK-006")
        {
            throw BackupInvalidProblem("バックアップのDB構成またはデータを安全に移行できませんでした。元ファイルと現在のFAQは変更していません。");
        }
        catch (Exception exception) when (exception is SqliteException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            throw BackupInvalidProblem("バックアップのDB構成またはデータを安全に移行できませんでした。元ファイルと現在のFAQは変更していません。");
        }
    }

    private static void ValidateRequiredBackupSchema(SqliteConnection connection, int version)
    {
        var expected = BackupSchemaShapes.GetOrAdd(version, ReadReferenceBackupSchema);
        var presentTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = tables.ExecuteReader();
            while (reader.Read()) presentTables.Add(reader.GetString(0));
        }
        foreach (var table in expected)
        {
            if (!presentTables.Contains(table.Name))
                throw BackupInvalidProblem("バックアップのDB版に必要なテーブルが不足しています。");
            var actual = ReadBackupTableShape(connection, table.Name);
            var columns = actual.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                if (!columns.TryGetValue(column.Name, out var candidate) ||
                    !string.Equals(column.Type, candidate.Type, StringComparison.OrdinalIgnoreCase) ||
                    column.NotNull != candidate.NotNull || column.PrimaryKey != candidate.PrimaryKey)
                    throw BackupInvalidProblem("バックアップのDB版に必要な列構成が不足または不整合です。");
            }
            if (table.ForeignKeys.Any(key => !actual.ForeignKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                throw BackupInvalidProblem("バックアップのDB版に必要な外部キーが不足しています。");
        }
        // Compare required logical structure, not SQL source text. Additional
        // tables/columns remain intact; SQLite's own FTS shadow layout is ignored.
    }

    private static IReadOnlyList<BackupTableShape> ReadReferenceBackupSchema(int version)
    {
        using var reference = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false
        }.ToString());
        reference.Open();
        for (var migration = 1; migration <= version; migration++) ApplyMigration(reference, migration);
        var names = new List<string>();
        using (var command = reference.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (!name.StartsWith("sqlite_", StringComparison.Ordinal) &&
                    !name.StartsWith("article_search_fts_", StringComparison.Ordinal)) names.Add(name);
            }
        }
        return names.Select(name => ReadBackupTableShape(reference, name)).ToArray();
    }

    private static BackupTableShape ReadBackupTableShape(SqliteConnection connection, string table)
    {
        var columns = new List<BackupColumnShape>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, type, [notnull], pk FROM pragma_table_info($table)";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(new BackupColumnShape(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        }
        var keys = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT [table], [from], [to], on_update, on_delete, [match] FROM pragma_foreign_key_list($table)";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                keys.Add(string.Join('\0', Enumerable.Range(0, 6)
                    .Select(index => reader.IsDBNull(index) ? string.Empty : reader.GetString(index))));
        }
        return new BackupTableShape(table, columns, keys);
    }

    private static void ValidateBackupMigrationHistory(SqliteConnection connection, int sourceVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version";
        using var reader = command.ExecuteReader();
        var expected = 1;
        while (reader.Read())
        {
            if (reader.GetInt64(0) != expected++)
                throw BackupInvalidProblem("バックアップのDB移行履歴が連続していません。");
        }
        if (expected != sourceVersion + 1)
            throw BackupInvalidProblem("バックアップのDB移行履歴が版情報と一致しません。");
    }

    private static bool RequiresLegacyBackupBootstrap(SqliteConnection connection, int sourceVersion)
    {
        if (sourceVersion < 6)
        {
            // A genuine pre-authentication schema has no users table or audit
            // columns. Do not interpret a mislabeled newer DB as a reset request.
            using var authenticationSchema = connection.CreateCommand();
            authenticationSchema.CommandText = """
                SELECT (SELECT COUNT(*) FROM sqlite_master WHERE name = 'users') +
                    (SELECT COUNT(*) FROM pragma_table_info('articles')
                        WHERE name IN ('created_by_user_id', 'updated_by_user_id'))
                """;
            if (Convert.ToInt64(authenticationSchema.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw BackupInvalidProblem("旧版バックアップの認証構成がDB版と一致しません。");
            return true;
        }

        using var users = connection.CreateCommand();
        users.CommandText = "SELECT COUNT(*), COALESCE(SUM(role = 'admin' AND is_active = 1), 0) FROM users";
        using var reader = users.ExecuteReader();
        reader.Read();
        var userCount = reader.GetInt64(0);
        var activeAdmins = reader.GetInt64(1);
        if (userCount != 0)
        {
            if (activeAdmins == 0)
                throw BackupInvalidProblem("バックアップに有効な管理者がいないため復元できません。利用者情報は初期化しません。");
            if (HasMissingBackupAudit(connection))
            {
                if (sourceVersion != 6)
                    throw BackupInvalidProblem("バックアップのFAQ監査情報が欠落しているため、安全に表示できません。");
                using var initialAdmin = connection.CreateCommand();
                initialAdmin.CommandText = "SELECT COUNT(*) FROM users WHERE id = $id";
                initialAdmin.Parameters.AddWithValue("$id", InitialAdminUserId);
                if (Convert.ToInt64(initialAdmin.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw BackupInvalidProblem("旧版バックアップの未設定の監査情報を引き継ぐ初期管理者が見つかりません。");
                // Rust also completes nullable v6 audit fields when users already
                // exist. Its fixed original administrator must be present; never
                // invent a replacement user or change non-null attribution.
                return true;
            }
            return false;
        }
        if (sourceVersion != 6)
            throw BackupInvalidProblem("バックアップの利用者情報が欠落しています。利用者情報は初期化しません。");

        // Rust can create a v6 migration safety backup before its first admin
        // bootstrap. Accept only that empty, wholly unaudited historical state.
        using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT COUNT(*) FROM articles WHERE created_by_user_id IS NOT NULL OR updated_by_user_id IS NOT NULL";
        if (Convert.ToInt64(audit.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw BackupInvalidProblem("旧版バックアップの利用者情報とFAQの監査情報が一致しません。");
        return true;
    }

    private static bool HasMissingBackupAudit(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM articles WHERE created_by_user_id IS NULL OR updated_by_user_id IS NULL)";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void ValidateBackupUsers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM users";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || !Argon2PasswordCodec.IsSupportedHash(reader.GetString(0)))
                throw BackupInvalidProblem("バックアップに現在の認証方式で確認できない利用者情報があります。パスワードは変更していません。");
        }
    }

    private static void ValidateBackupCategoryHierarchy(SqliteConnection connection)
    {
        var categories = new Dictionary<string, (string? Parent, int Depth)>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, parent_id, depth FROM categories";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                categories.Add(reader.GetString(0), (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2)));
        }
        foreach (var (id, category) in categories)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var depth = 1;
            var parent = category.Parent;
            while (parent is not null)
            {
                if (++depth > 5 || !seen.Add(parent) || !categories.TryGetValue(parent, out var ancestor))
                    throw BackupInvalidProblem("バックアップの分類階層が不正です。循環または5階層を超える分類は復元できません。");
                parent = ancestor.Parent;
            }
            if (category.Depth != depth)
                throw BackupInvalidProblem("バックアップの分類階層と階層番号が一致しません。");
        }
    }

    private static void ValidateBackupArticleContent(SqliteConnection connection, TransferBackupManifest manifest)
    {
        var files = manifest.Files.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var attachments = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, article_id, relative_path, media_type, byte_size, sha256 FROM article_attachments";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var relative = reader.GetString(2).Replace('\\', '/');
                var archivePath = $"attachments/articles/{relative}";
                ValidateBackupArchivePath(archivePath);
                if (Path.IsPathRooted(relative) ||
                    reader.GetString(3) is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif") ||
                    reader.GetInt64(4) is < 0 or > 10 * 1024 * 1024 ||
                    !files.TryGetValue(archivePath, out var file) ||
                    file.Size != reader.GetInt64(4) || file.Sha256 != reader.GetString(5))
                    throw BackupInvalidProblem("バックアップの添付画像とDB内の参照情報が一致しません。");
                attachments.Add(reader.GetString(0), reader.GetString(1));
            }
        }
        using var articles = connection.CreateCommand();
        articles.CommandText = "SELECT id, body_doc_json, body_format_version FROM articles";
        using var articleReader = articles.ExecuteReader();
        while (articleReader.Read())
        {
            if (articleReader.GetInt64(2) is < 1 or > RichTextBackupFormatVersion)
                throw BackupInvalidProblem("バックアップに未対応の回答形式が含まれています。");
            using var document = JsonDocument.Parse(articleReader.GetString(1),
                new JsonDocumentOptions { MaxDepth = SafeRichContentValidator.MaximumStoredJsonDepth });
            var content = SafeRichContentValidator.ValidateForBackup(document.RootElement);
            var articleId = articleReader.GetString(0);
            foreach (var reference in content.Attachments)
            {
                if (!attachments.TryGetValue(reference.Id, out var owner) || owner != articleId)
                    throw BackupInvalidProblem("バックアップの回答内画像とFAQの添付情報が一致しません。");
            }
        }
        // Do not rewrite JSON, plain text, legacy detail fields, or saved search
        // documents/FTS. Normal historical saves already populated those indexes.
    }

    private static void ValidateBackupSettings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_json FROM app_settings WHERE key IN ($appearance, $password)";
        command.Parameters.AddWithValue("$appearance", AppearanceSettingsKey);
        command.Parameters.AddWithValue("$password", PasswordPolicyKey);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0) == AppearanceSettingsKey)
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(reader.GetString(1), StrictSettingsJsonOptions)
                    ?? throw new JsonException();
                ValidateAppearanceSettings(settings, "SET-001");
            }
            else
            {
                _ = JsonSerializer.Deserialize<PasswordPolicySettings>(reader.GetString(1), SettingsJsonOptions)
                    ?? throw new JsonException();
            }
        }
        // Unrecognized legacy keys and external settings files stay opaque. They
        // are preserved, never interpreted as paths to company-managed documents.
    }

    private sealed record BackupColumnShape(string Name, string Type, long NotNull, long PrimaryKey);
    private sealed record BackupTableShape(
        string Name, IReadOnlyList<BackupColumnShape> Columns, IReadOnlyList<string> ForeignKeys);
}

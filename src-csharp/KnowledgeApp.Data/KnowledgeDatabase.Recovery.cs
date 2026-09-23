using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RecoveryTriggers = new(() =>
    {
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False");
        reference.Open();
        for (var version = 1; version <= MigrationCatalog.CurrentVersion; version++) ApplyMigration(reference, version);
        return ReadRecoveryTriggers(reference);
    });
    internal Guid RecoveryEpoch { get; private set; } = Guid.NewGuid();
    internal Action? RecoverySavingForTest { get; set; }

    internal long GetAuthenticationVersion(string id) => ExecuteLocked(() =>
    {
        var row = ReadRecovery(id);
        if (!row.Active) throw new AppProblemException(AppProblem.LoginRequired());
        return row.AuthVersion;
    });

    internal RecoveryKeyStatus GetRecoveryKeyStatus(string id) => ExecuteLocked(() =>
    {
        var row = RequireRecoveryAdmin(id);
        return new RecoveryKeyStatus(row.Hash is not null, row.Setup == "pending", row.IssuedAt);
    });

    internal RecoveryKeyStatus SkipRecoverySetup(string id) => ExecuteLocked(() =>
    {
        RequireRecoveryAdmin(id);
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE user_recovery_keys SET setup_state = 'skipped' WHERE user_id = $id AND setup_state = 'pending'";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return GetRecoveryKeyStatus(id);
    });

    internal IssuedRecoveryKey IssueRecoveryKey(string id, string? password, DateTimeOffset now) => ExecuteLocked(() =>
    {
        var row = RequireRecoveryAdmin(id);
        if (password is null || !Argon2PasswordCodec.Verify(password, row.PasswordHash))
            throw new AppProblemException(AppProblem.Authentication());
        var key = RecoveryKeyCodec.Generate();
        var hash = Argon2PasswordCodec.Hash(key);
        using var transaction = _connection.BeginTransaction();
        SaveRecoveryKey(id, hash, now, transaction);
        RecoverySavingForTest?.Invoke();
        transaction.Commit();
        return new IssuedRecoveryKey(RecoveryKeyCodec.Display(key));
    });

    internal RecoveryProof VerifyRecoveryKey(string? loginId, string? key, DateTimeOffset now) => ExecuteLocked(() =>
    {
        if (string.IsNullOrWhiteSpace(loginId) || loginId.Length > 100) throw RecoveryProblems.Invalid();
        string? id;
        using (var lookup = _connection.CreateCommand())
        {
            lookup.CommandText = "SELECT id FROM users WHERE normalized_login_id = $login";
            lookup.Parameters.AddWithValue("$login", NormalizeLoginId(loginId));
            id = lookup.ExecuteScalar() as string;
        }
        if (id is null) throw RecoveryProblems.Invalid();
        var row = ReadRecovery(id);
        if (!row.Active || row.Role != UserRoles.Admin) throw RecoveryProblems.Invalid();
        if (row.Hash is null) throw RecoveryProblems.Missing();
        if (row.BlockedUntil is not null && DateTimeOffset.Parse(row.BlockedUntil, CultureInfo.InvariantCulture) > now)
            throw RecoveryProblems.Blocked();
        var normalized = RecoveryKeyCodec.Normalize(key);
        if (normalized is null || !Argon2PasswordCodec.Verify(normalized, row.Hash))
        {
            var failures = row.BlockedUntil is null ? row.Failures + 1 : 1;
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE user_recovery_keys SET failed_attempts = $failures, blocked_until = $blocked WHERE user_id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$failures", failures);
            command.Parameters.AddWithValue("$blocked", failures >= 5 ? now.AddMinutes(15).ToString("O") : (object)DBNull.Value);
            command.ExecuteNonQuery(); // Persist failure before returning the error.
            throw failures >= 5 ? RecoveryProblems.Blocked() : RecoveryProblems.Invalid();
        }
        using (var clear = _connection.CreateCommand())
        {
            clear.CommandText = "UPDATE user_recovery_keys SET failed_attempts = 0, blocked_until = NULL WHERE user_id = $id";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        return new RecoveryProof(id, row.Generation, RecoveryEpoch);
    });

    internal IssuedRecoveryKey CompleteRecovery(RecoveryProof proof, string password, DateTimeOffset now) => ExecuteLocked(() =>
    {
        if (password.Length == 0) throw RecoveryProblems.Password();
        Argon2PasswordCodec.ValidateLength(password);
        var row = ReadRecovery(proof.UserId);
        if (proof.Epoch != RecoveryEpoch || row.Hash is null || row.Generation != proof.Generation || !row.Active || row.Role != UserRoles.Admin)
            throw RecoveryProblems.Expired();
        var passwordHash = Argon2PasswordCodec.Hash(password);
        var key = RecoveryKeyCodec.Generate();
        var keyHash = Argon2PasswordCodec.Hash(key);
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE users SET password_hash = $hash, updated_at = $now WHERE id = $id";
            command.Parameters.AddWithValue("$id", proof.UserId);
            command.Parameters.AddWithValue("$hash", passwordHash);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery(); // Trigger revokes the old key and authentication version.
        }
        SaveRecoveryKey(proof.UserId, keyHash, now, transaction);
        RecoverySavingForTest?.Invoke();
        transaction.Commit();
        return new IssuedRecoveryKey(RecoveryKeyCodec.Display(key));
    });

    private void SaveRecoveryKey(string id, string hash, DateTimeOffset now, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE user_recovery_keys SET key_hash = $hash, generation = generation + 1,
                issued_at = $now, setup_state = 'issued', failed_attempts = 0, blocked_until = NULL
            WHERE user_id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw RecoveryProblems.Expired();
    }

    private RecoveryRow RequireRecoveryAdmin(string id)
    {
        var row = ReadRecovery(id);
        if (!row.Active || row.Role != UserRoles.Admin) throw new AppProblemException(AppProblem.AdminRequired());
        return row;
    }

    private RecoveryRow ReadRecovery(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT u.role, u.is_active, u.password_hash, r.key_hash, r.generation, r.auth_version,
                   r.setup_state, r.issued_at, r.failed_attempts, r.blocked_until
            FROM users u JOIN user_recovery_keys r ON r.user_id = u.id WHERE u.id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw RecoveryProblems.Invalid();
        return new(reader.GetString(0), reader.GetInt64(1) == 1, reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private sealed record RecoveryRow(string Role, bool Active, string PasswordHash, string? Hash,
        long Generation, long AuthVersion, string Setup, string? IssuedAt, int Failures, string? BlockedUntil);

    private static void ValidateRecoveryRecords(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.key_hash, r.generation, r.auth_version, r.setup_state, r.issued_at, r.failed_attempts, r.blocked_until
            FROM users u LEFT JOIN user_recovery_keys r ON r.user_id = u.id
            """;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(1) || reader.GetInt64(1) < 0 || reader.GetInt64(2) < 0 ||
                    reader.GetString(3) is not ("pending" or "existing" or "skipped" or "issued") ||
                    reader.GetInt32(5) is < 0 or > 5 ||
                    (!reader.IsDBNull(0) && (!Argon2PasswordCodec.IsSupportedHash(reader.GetString(0)) || reader.IsDBNull(4))) ||
                    (!reader.IsDBNull(4) && !DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) ||
                    (!reader.IsDBNull(6) && !DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                    throw BackupInvalidProblem("復旧キーの保存情報を確認できませんでした。データは初期化していません。");
            }
        }
        var triggers = ReadRecoveryTriggers(connection);
        if (RecoveryTriggers.Value.Any(expected => !triggers.TryGetValue(expected.Key, out var actual) || actual != expected.Value))
            throw BackupInvalidProblem("復旧キーの失効処理を確認できませんでした。");
    }

    private static IReadOnlyDictionary<string, string> ReadRecoveryTriggers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND name IN ('users_create_recovery', 'users_invalidate_recovery')";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }
}

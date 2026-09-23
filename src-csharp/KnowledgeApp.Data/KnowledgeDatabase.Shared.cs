namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    public bool SharedAdministratorReady() => ExecuteLocked(() =>
    {
        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT password_hash FROM users WHERE role = 'admin' AND is_active = 1";
        using var reader = query.ExecuteReader();
        while (reader.Read()) if (!Argon2PasswordCodec.Verify(string.Empty, reader.GetString(0))) return true;
        return false;
    });

    private static bool HasNonemptyAdministrator(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT password_hash FROM users WHERE role = 'admin' AND is_active = 1";
        using var reader = query.ExecuteReader();
        while (reader.Read()) if (!Argon2PasswordCodec.Verify(string.Empty, reader.GetString(0))) return true;
        return false;
    }
    private void RequireSharedRestoreAdministrator(string snapshot)
    {
        if (!SharedMode) return;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = snapshot, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        if (!HasNonemptyAdministrator(connection)) throw BackupInvalidProblem("共有運用には空欄でない管理者パスワードが必要です。移行元で設定してからバックアップを作り直してください。現在のFAQは変更していません。");
    }

    public void InitializeSharedAdministrator(string password) => ExecuteLocked(() =>
    {
        if (!SharedMode || SharedAdministratorReady() || string.IsNullOrWhiteSpace(password))
            throw new AppProblemException(AppProblem.Authentication());
        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM users";
        if (Convert.ToInt64(query.ExecuteScalar()) != 1) throw new AppProblemException(AppProblem.Authentication());
        ResetUserPassword(InitialAdminUserId, password);
        SavePasswordPolicy(new(false));
    });
}

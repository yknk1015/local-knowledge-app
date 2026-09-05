using System.Security.Cryptography;

namespace KnowledgeApp.Data;

public sealed class BackupService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly string _dataRoot;
    private readonly Action? _afterRestore;
    private readonly TimeProvider _clock;
    private readonly object _confirmationSync = new();
    private readonly Dictionary<string, Confirmation> _confirmations = new(StringComparer.Ordinal);
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumConfirmations = 16;

    public BackupService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        string dataRoot,
        Action? afterRestore = null)
        : this(database, authentication, dataRoot, afterRestore, TimeProvider.System) { }

    internal BackupService(KnowledgeDatabase database, AuthenticationService authentication,
        string dataRoot, Action? afterRestore, TimeProvider clock)
    {
        _database = database;
        _authentication = authentication;
        _dataRoot = Path.GetFullPath(dataRoot);
        _afterRestore = afterRestore;
        _clock = clock;
    }

    public BackupOverview GetOverview()
    {
        _authentication.RequireAdmin();
        return _database.GetFullBackupOverview(_dataRoot);
    }

    public BackupResult CreateFullBackup(CreateFullBackupInput input)
    {
        _authentication.RequireAdmin();
        return _database.CreateFullBackup(_dataRoot, input, rememberDestination: true);
    }

    public BackupPreview InspectBackup(string sourcePath)
    {
        var session = _authentication.RequireAdmin();
        var verified = _database.InspectFullBackupWithIdentity(_dataRoot, sourcePath);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        lock (_confirmationSync)
        {
            var now = _clock.GetUtcNow();
            foreach (var stale in _confirmations.Where(item => item.Value.ExpiresAt <= now ||
                         !ReferenceEquals(item.Value.Session, session)).Select(item => item.Key).ToArray())
                _confirmations.Remove(stale);
            while (_confirmations.Count >= MaximumConfirmations)
                _confirmations.Remove(_confirmations.MinBy(item => item.Value.ExpiresAt).Key);
            _confirmations.Add(token, new(session, verified.Preview.SourcePath, verified.FileSha256, now + ConfirmationLifetime));
        }
        return verified.Preview with { ConfirmationToken = token };
    }

    // The WebView dispatcher must use this API. The path-only overload below is
    // retained for trusted internal compatibility checks, not exposed to the UI.
    public RestoreResult RestoreConfirmedBackup(RestoreConfirmedBackupInput input) =>
        _authentication.ExecuteForAdminSession(session =>
        {
            Confirmation confirmation;
            lock (_confirmationSync)
            {
                if (input.ConfirmationToken is not { Length: 64 } token ||
                    !_confirmations.Remove(token, out confirmation!)) throw ReconfirmationRequired();
            }
            // Consume before every further check: failed requests cannot replay it.
            string path;
            try { path = FileSystemBoundary.ValidatePathSyntax(input.Path); }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
            { throw ReconfirmationRequired(); }
            if (!ReferenceEquals(confirmation.Session, session) || confirmation.ExpiresAt <= _clock.GetUtcNow() ||
                !string.Equals(confirmation.Path, path, StringComparison.OrdinalIgnoreCase)) throw ReconfirmationRequired();
            var result = _database.RestoreFullBackup(_dataRoot, path, confirmation.FileSha256);
            _authentication.Logout();
            _afterRestore?.Invoke();
            return result;
        });

    public RestoreResult RestoreBackup(string sourcePath)
    {
        _authentication.RequireAdmin();
        var result = _database.RestoreFullBackup(_dataRoot, sourcePath);
        _authentication.Logout();
        _afterRestore?.Invoke();
        return result;
    }

    internal static AppProblemException ReconfirmationRequired() => new(new AppProblem(
        "BK-011",
        "復元対象の確認が無効になりました。現在のデータは変更していません。",
        "バックアップを選び直して内容を確認し、10分以内に復元してください。確認後のファイル変更や再ログイン、復元の再試行には再確認が必要です。"));

    private sealed record Confirmation(AuthenticatedUser Session, string Path, string FileSha256, DateTimeOffset ExpiresAt);
}

namespace KnowledgeApp.Data;

public sealed partial class AuthenticationService
{
    private readonly KnowledgeDatabase _database;
    private readonly object _sessionSync = new();
    private AuthenticatedUser? _currentUser;
    private long _sessionVersion;
    private Guid _sessionEpoch;
    internal Action? AuthenticatedForTest { get; set; }

    public AuthenticationService(KnowledgeDatabase database)
    {
        _database = database;
    }

    public AuthenticatedUser Login(string loginId, string password)
    {
        if (string.IsNullOrWhiteSpace(loginId))
        {
            throw new AppProblemException(AppProblem.Authentication());
        }
        lock (_sessionSync)
        {
            // Keep authentication and publishing the session indivisible with a
            // confirmed restore; never publish a user authenticated in an old DB.
            var user = _database.Authenticate(loginId, password);
            AuthenticatedForTest?.Invoke();
            _currentUser = user;
            _sessionVersion = _database.GetAuthenticationVersion(user.Id);
            _sessionEpoch = _database.RecoveryEpoch;
            _recoveryGrant = null;
            return user;
        }
    }

    public void Logout()
    {
        lock (_sessionSync)
        {
            _currentUser = null;
            _recoveryGrant = null;
        }
    }

    public AuthenticatedUser? GetCurrentUser()
    {
        lock (_sessionSync)
        {
            if (_currentUser is not null && !SessionIsCurrent()) Logout();
            return _currentUser;
        }
    }

    public IReadOnlyList<UserSummary> ListUsers()
    {
        RequireAdmin();
        return _database.ListUsers();
    }

    public UserSummary CreateUser(string loginId, string displayName, string password, string role)
    {
        RequireAdmin();
        return _database.CreateUser(loginId, displayName, password, role);
    }

    public UserSummary SetUserActive(string id, bool isActive)
    {
        var current = RequireAdmin();
        if (current.Id == id && !isActive)
        {
            throw new AppProblemException(new AppProblem(
                "USR-002",
                "ログイン中の利用者自身は利用停止にできません。",
                "別の管理者でログインしてから利用停止にしてください。"));
        }
        return _database.SetUserActive(id, isActive);
    }

    public UserSummary ResetUserPassword(string id, string password)
    {
        return ExecuteForAdminSession(_ =>
        {
            var result = _database.ResetUserPassword(id, password);
            if (_currentUser?.Id == id) _currentUser = null;
            _recoveryGrant = null;
            return result;
        });
    }

    public PasswordPolicySettings GetPasswordPolicy()
    {
        RequireAdmin();
        return _database.GetPasswordPolicy();
    }

    public PasswordPolicySettings SavePasswordPolicy(PasswordPolicySettings settings)
    {
        RequireAdmin();
        return _database.SavePasswordPolicy(settings);
    }

    public AuthenticatedUser RequireUser()
    {
        lock (_sessionSync)
        {
            return GetCurrentUser() ?? throw new AppProblemException(AppProblem.LoginRequired());
        }
    }

    public AuthenticatedUser RequireEditor()
    {
        var user = RequireUser();
        if (user.Role is not (UserRoles.Admin or UserRoles.Editor))
            throw new AppProblemException(new AppProblem("AUTH-004", "FAQ編集権限が必要です。", "管理者へ権限の変更を相談してください。"));
        return user;
    }

    public UserSummary SetUserRole(string id, string role) => ExecuteForAdminSession(_ => _database.SetUserRole(id, role));

    public AuthenticatedUser RequireAdmin()
    {
        var user = RequireUser();
        if (user.Role != UserRoles.Admin)
        {
            throw new AppProblemException(AppProblem.AdminRequired());
        }
        return user;
    }

    // Login creates a new user instance even for the same account. Holding the
    // existing session lock prevents a confirmed restore from crossing logins.
    internal T ExecuteForEditorSession<T>(Func<AuthenticatedUser, T> operation)
    {
        lock (_sessionSync) return operation(RequireEditor());
    }

    internal T ExecuteForAdminSession<T>(Func<AuthenticatedUser, T> operation)
    {
        lock (_sessionSync)
        {
            return operation(RequireAdmin());
        }
    }

    private bool SessionIsCurrent()
    {
        try { return _sessionEpoch == _database.RecoveryEpoch &&
            _sessionVersion == _database.GetAuthenticationVersion(_currentUser!.Id); }
        catch (AppProblemException) { return false; }
    }
}

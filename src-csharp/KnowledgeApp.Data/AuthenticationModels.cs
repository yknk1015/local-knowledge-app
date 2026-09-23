namespace KnowledgeApp.Data;

public static class UserRoles
{
    public const string Admin = "admin";
    public const string Editor = "editor";
    public const string Viewer = "viewer";
    // Source compatibility for synthetic legacy callers; stored roles use editor.
    public const string User = Editor;

    public static bool IsValid(string role) => role is Admin or Editor or Viewer;
}

public sealed record AuthenticatedUser(
    string Id,
    string LoginId,
    string DisplayName,
    string Role);

public sealed record UserSummary(
    string Id,
    string LoginId,
    string DisplayName,
    string Role,
    bool IsActive,
    string CreatedAt,
    string UpdatedAt,
    string? LastLoginAt);

public sealed record PasswordPolicySettings(bool AllowEmptyPasswords)
{
    public static PasswordPolicySettings Default { get; } = new(true);
}

public sealed record SyntheticDatabaseOpenInfo(
    string DatabasePath,
    int PreviousSchemaVersion,
    int CurrentSchemaVersion,
    bool Created,
    string? MigrationBackupPath);

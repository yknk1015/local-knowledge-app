namespace KnowledgeApp.Data;

public static class UserRoles
{
    public const string Admin = "admin";
    public const string User = "user";

    public static bool IsValid(string role) => role is Admin or User;
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

using System.Text.Json.Serialization;

namespace KnowledgeApp.Data;

public sealed record RecoveryKeyStatus(bool HasKey, bool NeedsSetup, string? IssuedAt);
public sealed record IssuedRecoveryKey(string Key);
public sealed record RecoveryAuthorization(string Token, string ExpiresAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IssueRecoveryKeyInput(string? CurrentPassword);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VerifyRecoveryKeyInput(string? LoginId, string? Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompletePasswordRecoveryInput(string? Token, string? NewPassword, string? ConfirmPassword);

internal sealed record RecoveryProof(string UserId, long Generation, Guid Epoch);

internal static class RecoveryProblems
{
    internal static AppProblemException Invalid() => new(new("REC-001",
        "ログインIDまたは復旧キーを確認できませんでした。", "入力を確認してください。一般利用者は管理者へ再設定を依頼してください。"));
    internal static AppProblemException Missing() => new(new("REC-002",
        "復旧キーが作成されていません。", "別の有効な管理者へパスワードの再設定を依頼してください。"));
    internal static AppProblemException Blocked() => new(new("REC-003",
        "復旧キーの入力を一時的に制限しています。", "15分後にもう一度お試しください。通常のパスワードでのログインは利用できます。"));
    internal static AppProblemException Expired() => new(new("REC-004",
        "パスワード再設定の確認が無効になりました。", "復旧キーを入力し直し、5分以内に新しいパスワードを設定してください。"));
    internal static AppProblemException Password() => new(new("REC-005",
        "新しいパスワードを確認してください。", "空欄にせず、確認欄にも同じパスワードを入力してください。"));
}

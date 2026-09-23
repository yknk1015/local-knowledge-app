using System.Security.Cryptography;

namespace KnowledgeApp.Data;

public sealed partial class AuthenticationService
{
    private TimeProvider _clock = TimeProvider.System;
    private RecoveryGrant? _recoveryGrant;

    internal AuthenticationService(KnowledgeDatabase database, TimeProvider clock) : this(database) => _clock = clock;

    public RecoveryKeyStatus GetRecoveryKeyStatus() => ExecuteForAdminSession(user => _database.GetRecoveryKeyStatus(user.Id));
    public RecoveryKeyStatus SkipRecoverySetup() => ExecuteForAdminSession(user => _database.SkipRecoverySetup(user.Id));

    public IssuedRecoveryKey IssueRecoveryKey(IssueRecoveryKeyInput input) => ExecuteForAdminSession(user =>
    {
        var result = _database.IssueRecoveryKey(user.Id, input.CurrentPassword, _clock.GetUtcNow());
        _recoveryGrant = null;
        return result;
    });

    public RecoveryAuthorization VerifyRecoveryKey(VerifyRecoveryKeyInput input)
    {
        lock (_sessionSync)
        {
            if (GetCurrentUser() is not null) throw RecoveryProblems.Invalid();
            _recoveryGrant = null;
            var proof = _database.VerifyRecoveryKey(input.LoginId, input.Key, _clock.GetUtcNow());
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var expiry = _clock.GetUtcNow().AddMinutes(5);
            _recoveryGrant = new(token, proof, _clock.GetTimestamp(), expiry);
            return new(token, expiry.ToString("O"));
        }
    }

    public IssuedRecoveryKey CompletePasswordRecovery(CompletePasswordRecoveryInput input)
    {
        lock (_sessionSync)
        {
            var grant = _recoveryGrant;
            if (grant is null || input.Token is not { Length: 64 } || input.Token != grant.Token ||
                grant.ExpiresAt <= _clock.GetUtcNow() || _clock.GetElapsedTime(grant.StartedAt) >= TimeSpan.FromMinutes(5))
            {
                _recoveryGrant = null;
                throw RecoveryProblems.Expired();
            }
            if (string.IsNullOrEmpty(input.NewPassword) || input.NewPassword != input.ConfirmPassword)
                throw RecoveryProblems.Password();
            var result = _database.CompleteRecovery(grant.Proof, input.NewPassword, _clock.GetUtcNow());
            Logout();
            return result;
        }
    }

    public bool CancelPasswordRecovery()
    {
        lock (_sessionSync) { _recoveryGrant = null; return true; }
    }

    private sealed record RecoveryGrant(string Token, RecoveryProof Proof, long StartedAt, DateTimeOffset ExpiresAt);
}

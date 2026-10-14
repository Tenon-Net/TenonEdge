using System.Collections.Concurrent;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 修改自己的密码（总设计 §10.3，功能需求 §1.4）：三种角色都可以，须输入原密码；新密码按密码规则检查，且不能与原密码相同。
/// <list type="bullet">
/// <item>原密码错误按账号在内存中单独计数：连续 <see cref="LoginLockout.MaximumFailures"/> 次时更换安全戳，结束该账号的全部会话（包括当前会话）。
/// 拿到会话的人最多猜几次原密码，会话随即失效；这个计数只能由已登录的会话触发。</item>
/// <item>登录锁定不阻止已登录的用户修改密码：登录锁定可以由网络上的任何人触发并一直维持，若因此不能改密码，
/// 用户就无法借改密码结束被窃取的会话。</item>
/// <item>与登录共用按账号名排队（<see cref="LoginLockout.EnterAsync"/>）与按来源地址的限流。</item>
/// <item>写入时核对密码哈希仍是校验时的那一个，期间被其他操作改过则按原密码不正确处理。</item>
/// <item>修改后更换安全戳，使该账号的其他会话失效，清除“须修改密码”标记与原密码错误计数；当前会话由调用方重新签发。</item>
/// </list>
/// </summary>
internal sealed class AccountService(
    ConfigDatabase database,
    PasswordHashing hashing,
    LoginLockout lockout,
    AuditTrail audit,
    SessionSecurityState sessions)
{
    private readonly ConcurrentDictionary<long, int> _wrongCurrentPasswords = new();

    internal void ClearPasswordAttempts(long userId) => _wrongCurrentPasswords.TryRemove(userId, out _);

    public async Task<PasswordChangeOutcome> ChangePasswordAsync(
        long userId,
        string expectedSecurityStamp,
        string? currentPassword,
        string? newPassword,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var user = database.Client.Queryable<UserAccountRecord>().InSingle(userId);
        if (user is null || !user.IsEnabled || user.SecurityStamp != expectedSecurityStamp)
        {
            return new PasswordChangeOutcome.AccountUnavailable();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(currentPassword))
        {
            errors["currentPassword"] = ["请输入原密码。"];
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            errors["newPassword"] = ["请输入新密码。"];
        }
        else if (PasswordPolicy.Validate(newPassword, user.AccountName) is { } reason)
        {
            errors["newPassword"] = [reason];
        }
        else if (newPassword == currentPassword)
        {
            errors["newPassword"] = ["新密码不能与原密码相同。"];
        }

        if (errors.Count > 0)
        {
            return new PasswordChangeOutcome.Invalid(errors);
        }

        using var attempt = await lockout.EnterAsync(user.NormalizedAccountName, cancellationToken);
        user = database.Client.Queryable<UserAccountRecord>().InSingle(userId);
        if (user is null || !user.IsEnabled || user.SecurityStamp != expectedSecurityStamp)
        {
            return new PasswordChangeOutcome.AccountUnavailable();
        }

        if (!hashing.Verify(user.PasswordHash, currentPassword!, out _))
        {
            return WrongCurrentPassword(user, sourceIp);
        }

        var verifiedHash = user.PasswordHash;
        var passwordHash = hashing.Hash(newPassword!);
        var updated = database.InTransaction<UserAccountRecord?>(client =>
        {
            var fresh = client.Queryable<UserAccountRecord>().InSingle(userId);
            if (fresh is null || !fresh.IsEnabled || fresh.SecurityStamp != expectedSecurityStamp || fresh.PasswordHash != verifiedHash)
            {
                return null;
            }

            fresh.PasswordHash = passwordHash;
            fresh.SecurityStamp = SecurityStamps.New();
            fresh.MustChangePassword = false;
            client.Updateable(fresh)
                .UpdateColumns(record => new { record.PasswordHash, record.SecurityStamp, record.MustChangePassword })
                .ExecuteCommand();
            audit.Append(client, new AuditEntry(AuditActions.PasswordChanged, AuditResults.Success, fresh.AccountName, sourceIp, Target: fresh.AccountName));
            return fresh;
        });
        if (updated is null)
        {
            return new PasswordChangeOutcome.Invalid(CurrentPasswordIncorrect());
        }

        _wrongCurrentPasswords.TryRemove(userId, out _);
        sessions.InvalidateUser(userId);
        return new PasswordChangeOutcome.Changed(updated);
    }

    private PasswordChangeOutcome WrongCurrentPassword(UserAccountRecord user, string? sourceIp)
    {
        var failures = _wrongCurrentPasswords.AddOrUpdate(user.Id, 1, (_, count) => count + 1);
        if (failures < LoginLockout.MaximumFailures)
        {
            audit.AppendOrLog(new AuditEntry(AuditActions.PasswordChanged, AuditResults.Failure, user.AccountName, sourceIp, Detail: "原密码不正确"));
            return new PasswordChangeOutcome.Invalid(CurrentPasswordIncorrect());
        }

        database.InTransaction(client =>
        {
            client.Ado.ExecuteCommand(
                $"UPDATE {UserAccountRecord.TableName} SET security_stamp = @stamp WHERE id = @id",
                new { stamp = SecurityStamps.New(), id = user.Id });
            audit.Append(client, new AuditEntry(
                AuditActions.PasswordChanged,
                AuditResults.Failure,
                user.AccountName,
                sourceIp,
                Target: user.AccountName,
                Detail: $"原密码连续错误 {LoginLockout.MaximumFailures} 次，已结束该账号的全部会话"));
            return true;
        });
        _wrongCurrentPasswords.TryRemove(user.Id, out _);
        sessions.InvalidateUser(user.Id);
        return new PasswordChangeOutcome.SessionsEnded();
    }

    private static Dictionary<string, string[]> CurrentPasswordIncorrect() => new() { ["currentPassword"] = ["原密码不正确。"] };
}

/// <summary>修改密码的结果。</summary>
internal abstract record PasswordChangeOutcome
{
    public sealed record Changed(UserAccountRecord User) : PasswordChangeOutcome;

    public sealed record Invalid(IDictionary<string, string[]> Errors) : PasswordChangeOutcome;

    /// <summary>会话对应的账号已不存在、已停用或安全戳已变化。</summary>
    public sealed record AccountUnavailable : PasswordChangeOutcome;

    /// <summary>原密码连续错误达到上限，已更换安全戳，该账号的全部会话（包括当前会话）已结束。</summary>
    public sealed record SessionsEnded : PasswordChangeOutcome;
}

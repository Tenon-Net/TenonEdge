using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 登录（总设计 §3.3，功能需求 §1.4）：失败统一提示“账号或密码错误”，不区分账号是否存在；同一账号名连续 5 次失败锁定 15 分钟，
/// 不存在的账号名同样计数（<see cref="LoginLockout"/>）；成功时清零计数并记录最后登录时间与来源 IP。
/// <list type="bullet">
/// <item>同一账号名的尝试逐个处理（<see cref="LoginLockout.EnterAsync"/>），账号记录在轮到之后才读取，锁定一旦提交即对下一次尝试生效。</item>
/// <item>不存在、已停用或没有密码的账号也执行一次等量的哈希校验。已有账号失败时另有一次计数写入，存储较慢时响应时间可能仍有差别。</item>
/// <item>审计单独写入，写入失败时改写到日志，不阻断登录（总设计 §3.3）；不存在的账号名不写入审计，
/// 以免把误输入账号栏的密码记下来。计数与最后登录信息写入失败同样只记日志，条数受 <see cref="StorageWriteFallback"/> 限制。</item>
/// </list>
/// </summary>
internal sealed partial class LoginService(
    ConfigDatabase database,
    PasswordHashing hashing,
    LoginLockout lockout,
    AuditTrail audit,
    StorageWriteFallback fallback,
    TimeProvider time,
    ILogger<LoginService> logger)
{
    private const int MaximumTrackedNameLength = 64;

    private const string CounterFallbackKind = "登录计数";

    public async Task<LoginOutcome> LoginAsync(string accountName, string password, string? sourceIp, CancellationToken cancellationToken)
    {
        var name = accountName.Trim();
        var normalized = AccountNames.Normalize(name.Length > MaximumTrackedNameLength ? name[..MaximumTrackedNameLength] : name);
        using var attempt = await lockout.EnterAsync(normalized, cancellationToken);
        var user = AccountNames.IsValid(name)
            ? database.Client.Queryable<UserAccountRecord>().First(record => record.NormalizedAccountName == normalized)
            : null;
        if (user is null)
        {
            return FailUnknownAccount(normalized, password, sourceIp);
        }

        var now = time.GetUtcNow();
        if (AccountLockouts.LockedUntil(user, now) is { } lockedUntil)
        {
            audit.AppendOrLog(new AuditEntry(AuditActions.Login, AuditResults.Failure, user.AccountName, sourceIp, Detail: "账号处于锁定期"));
            return new LoginOutcome.LockedOut(lockedUntil);
        }

        var passwordMatches = hashing.Verify(user.PasswordHash, password, out var needsRehash);
        return passwordMatches && user.IsEnabled
            ? Succeed(user, password, needsRehash, sourceIp, now)
            : Fail(user, sourceIp, now);
    }

    private LoginOutcome FailUnknownAccount(string normalized, string password, string? sourceIp)
    {
        if (lockout.UnknownNameLockedUntil(normalized) is { } lockedUntil)
        {
            audit.AppendOrLog(new AuditEntry(AuditActions.Login, AuditResults.Failure, AccountName: null, sourceIp, Detail: "账号不存在，处于锁定期"));
            return new LoginOutcome.LockedOut(lockedUntil);
        }

        hashing.Verify(null, password, out _);
        var lockedNow = lockout.RecordUnknownNameFailure(normalized);
        audit.AppendOrLog(new AuditEntry(AuditActions.Login, AuditResults.Failure, AccountName: null, sourceIp, Detail: "账号不存在"));
        return lockedNow is { } until ? new LoginOutcome.LockedOut(until) : new LoginOutcome.InvalidCredentials();
    }

    private LoginOutcome Fail(UserAccountRecord user, string? sourceIp, DateTimeOffset now)
    {
        DateTimeOffset? lockedUntil = null;
        try
        {
            lockedUntil = AccountLockouts.RecordFailure(database, user.Id, now);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportCounterUpdateFailed(user.AccountName, exception);
        }

        var detail = !user.IsEnabled ? "账号已停用" : user.PasswordHash is null ? "账号没有密码（等待重新设置）" : "密码错误";
        audit.AppendOrLog(new AuditEntry(
            AuditActions.Login,
            AuditResults.Failure,
            user.AccountName,
            sourceIp,
            Detail: lockedUntil is null ? detail : detail + "；连续失败达到上限，锁定 15 分钟"));
        return lockedUntil is { } locked ? new LoginOutcome.LockedOut(locked) : new LoginOutcome.InvalidCredentials();
    }

    private LoginOutcome Succeed(UserAccountRecord user, string password, bool needsRehash, string? sourceIp, DateTimeOffset now)
    {
        var signedIn = user;
        try
        {
            var outcome = database.InTransaction<LoginOutcome>(client =>
            {
                var fresh = client.Queryable<UserAccountRecord>().InSingle(user.Id);
                if (fresh.LockoutEnd is { } lockoutEnd && lockoutEnd > now.ToUnixTimeMilliseconds())
                {
                    return new LoginOutcome.LockedOut(DateTimeOffset.FromUnixTimeMilliseconds(lockoutEnd));
                }

                fresh.FailedLoginCount = 0;
                fresh.LockoutEnd = null;
                fresh.LastLoginAt = now.ToUnixTimeMilliseconds();
                fresh.LastLoginIp = sourceIp;
                if (needsRehash)
                {
                    fresh.PasswordHash = hashing.Hash(password);
                }

                client.Updateable(fresh)
                    .UpdateColumns(record => new { record.FailedLoginCount, record.LockoutEnd, record.LastLoginAt, record.LastLoginIp, record.PasswordHash })
                    .ExecuteCommand();
                return new LoginOutcome.Succeeded(fresh);
            });
            if (outcome is not LoginOutcome.Succeeded succeeded)
            {
                return outcome;
            }

            signedIn = succeeded.User;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportCounterUpdateFailed(user.AccountName, exception);
        }

        audit.AppendOrLog(new AuditEntry(AuditActions.Login, AuditResults.Success, signedIn.AccountName, sourceIp));
        return new LoginOutcome.Succeeded(signedIn);
    }

    private void ReportCounterUpdateFailed(string accountName, Exception exception)
    {
        if (fallback.TryAcquire(CounterFallbackKind))
        {
            LogCounterUpdateFailed(logger, accountName, exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "无法更新账号 {AccountName} 的登录计数或最后登录信息：{Reason}")]
    private static partial void LogCounterUpdateFailed(ILogger logger, string accountName, string reason);
}

/// <summary>登录结果。</summary>
internal abstract record LoginOutcome
{
    public sealed record Succeeded(UserAccountRecord User) : LoginOutcome;

    public sealed record InvalidCredentials : LoginOutcome;

    public sealed record LockedOut(DateTimeOffset Until) : LoginOutcome;
}

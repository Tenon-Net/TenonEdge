using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 已有账号的失败计数与锁定（总设计 §3.3）：登录时密码错误与修改密码时原密码错误计入同一计数，连续 5 次锁定 15 分钟。
/// 调用方须已进入该账号名在 <see cref="LoginLockout.EnterAsync"/> 中的队列。
/// </summary>
internal static class AccountLockouts
{
    /// <summary>账号记录中的锁定仍在生效时返回锁定到的时间，否则返回 null。</summary>
    public static DateTimeOffset? LockedUntil(UserAccountRecord user, DateTimeOffset now) =>
        user.LockoutEnd is { } lockoutEnd && lockoutEnd > now.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(lockoutEnd)
            : null;

    /// <summary>在一个写事务中记一次失败；达到上限时锁定并清零计数，返回锁定到的时间。</summary>
    public static DateTimeOffset? RecordFailure(ConfigDatabase database, long userId, DateTimeOffset now) =>
        database.InTransaction(client =>
        {
            var fresh = client.Queryable<UserAccountRecord>().InSingle(userId);
            fresh.FailedLoginCount++;
            DateTimeOffset? until = null;
            if (fresh.FailedLoginCount >= LoginLockout.MaximumFailures)
            {
                until = now + LoginLockout.Duration;
                fresh.FailedLoginCount = 0;
                fresh.LockoutEnd = until.Value.ToUnixTimeMilliseconds();
            }

            client.Updateable(fresh).UpdateColumns(record => new { record.FailedLoginCount, record.LockoutEnd }).ExecuteCommand();
            return until;
        });
}

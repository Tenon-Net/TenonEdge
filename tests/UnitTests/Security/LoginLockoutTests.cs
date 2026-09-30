using TenonEdge.Host.Security;

namespace TenonEdge.UnitTests.Security;

/// <summary>
/// 不存在的账号名的失败计数（总设计 §3.3）：与已有账号一样，连续 5 次失败锁定 15 分钟；锁定期间的尝试不延长锁定。
/// </summary>
public sealed class LoginLockoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FifthConsecutiveFailureLocksTheNameForFifteenMinutes()
    {
        var time = new SettableTime(Now);
        var lockout = new LoginLockout(time);

        for (var attempt = 1; attempt < LoginLockout.MaximumFailures; attempt++)
        {
            Assert.Null(lockout.RecordUnknownNameFailure("nobody"));
        }

        Assert.Equal(Now + LoginLockout.Duration, lockout.RecordUnknownNameFailure("nobody"));
        Assert.Equal(Now + LoginLockout.Duration, lockout.UnknownNameLockedUntil("nobody"));
        Assert.Null(lockout.UnknownNameLockedUntil("someone-else"));
    }

    [Fact]
    public void AttemptsDuringTheLockoutDoNotExtendIt()
    {
        var time = new SettableTime(Now);
        var lockout = new LoginLockout(time);
        for (var attempt = 0; attempt < LoginLockout.MaximumFailures; attempt++)
        {
            lockout.RecordUnknownNameFailure("nobody");
        }

        time.Value = Now + TimeSpan.FromMinutes(10);
        Assert.Equal(Now + LoginLockout.Duration, lockout.RecordUnknownNameFailure("nobody"));

        time.Value = Now + LoginLockout.Duration;
        Assert.Null(lockout.UnknownNameLockedUntil("nobody"));
        Assert.Null(lockout.RecordUnknownNameFailure("nobody"));
    }

    [Fact]
    public void TrackedUnknownNamesStayWithinTheCapByEvictingTheOldestFailure()
    {
        var time = new SettableTime(Now);
        var lockout = new LoginLockout(time);
        for (var index = 0; index < LoginLockout.MaximumTrackedUnknownNames; index++)
        {
            lockout.RecordUnknownNameFailure($"name-{index}");
            time.Value += TimeSpan.FromMilliseconds(1);
        }

        // 全部条目都未过期时，新的账号名挤掉最早失败的那一个，总数不超过上限。
        lockout.RecordUnknownNameFailure("newcomer");
        Assert.Equal(LoginLockout.MaximumTrackedUnknownNames, lockout.TrackedUnknownNames);

        // name-0 已被移除、从头计数：再失败 4 次也未达到 5 次。
        for (var attempt = 1; attempt < LoginLockout.MaximumFailures; attempt++)
        {
            Assert.Null(lockout.RecordUnknownNameFailure("name-0"));
        }

        Assert.Equal(LoginLockout.MaximumTrackedUnknownNames, lockout.TrackedUnknownNames);
    }

    [Fact]
    public void ExpiredUnknownNamesAreRemovedBeforeAnyActiveOne()
    {
        var time = new SettableTime(Now);
        var lockout = new LoginLockout(time);
        for (var index = 0; index < LoginLockout.MaximumTrackedUnknownNames; index++)
        {
            lockout.RecordUnknownNameFailure($"name-{index}");
        }

        time.Value = Now + LoginLockout.Duration;
        lockout.RecordUnknownNameFailure("later");

        Assert.Equal(1, lockout.TrackedUnknownNames);
    }

    private sealed class SettableTime(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = value;

        public override DateTimeOffset GetUtcNow() => Value;
    }
}

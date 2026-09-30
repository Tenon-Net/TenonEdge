using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Security;

/// <summary>
/// 会话校验缓存与失效并发时的顺序（T0-08；总设计 §3.3）：读取账号状态的过程中该账号被标记失效，
/// 读到的旧安全戳不得写入缓存，否则修改密码后旧会话继续有效、新会话反被拒绝。
/// </summary>
public sealed class SessionSecurityStateTests
{
    private const long UserId = 42;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InvalidationDuringALoadIsNotOverwrittenByTheStaleValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var store = TestConfigStore.Create();
        store.Initialize();
        using var loading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var loads = 0;
        var state = new SessionSecurityState(store.Database, _ =>
        {
            if (Interlocked.Increment(ref loads) == 1)
            {
                // 第一次读取发生在修改密码提交之前，读到旧安全戳；在写入缓存之前暂停。
                loading.Set();
                Assert.True(release.Wait(WaitTimeout, cancellationToken));
                return new UserSecurity(true, true, "stamp-before-change", nameof(UserRole.Administrator));
            }

            return new UserSecurity(true, true, "stamp-after-change", nameof(UserRole.Administrator));
        });

        var reader = Task.Run(() => state.GetUser(UserId), cancellationToken);
        Assert.True(loading.Wait(WaitTimeout, cancellationToken));
        state.InvalidateUser(UserId);
        release.Set();

        Assert.Equal("stamp-after-change", (await reader).SecurityStamp);
        Assert.Equal("stamp-after-change", state.GetUser(UserId).SecurityStamp);
        Assert.Equal(2, Volatile.Read(ref loads));
    }

    [Fact]
    public void CachedValueIsReusedUntilTheUserIsInvalidated()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var loads = 0;
        var state = new SessionSecurityState(store.Database, _ =>
            new UserSecurity(true, true, $"stamp-{Interlocked.Increment(ref loads)}", nameof(UserRole.Viewer)));

        Assert.Equal("stamp-1", state.GetUser(UserId).SecurityStamp);
        Assert.Equal("stamp-1", state.GetUser(UserId).SecurityStamp);
        state.InvalidateUser(UserId);
        Assert.Equal("stamp-2", state.GetUser(UserId).SecurityStamp);
        Assert.Equal(2, loads);
    }
}

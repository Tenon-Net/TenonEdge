using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Features.Users;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Users;

public sealed partial class UserManagementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedManagementOperationRechecksAdministratorInsideTheWriteTransaction(bool create)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var actor = TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "survivor", ApiRequests.StrongPassword, UserRole.Administrator);
        var target = TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        var lockout = factory.Services.GetRequiredService<LoginLockout>();
        var service = factory.Services.GetRequiredService<UserManagementService>();
        using var gate = await lockout.EnterAsync(AccountNames.Normalize(create ? "new-user" : "target"), Token);
        var pending = create
            ? service.CreateAsync(new("new-user", null, UserRole.Viewer, ApiRequests.StrongPassword), new(actor.Id, actor.SecurityStamp, null), Token)
            : service.ChangeAsync(target.Id, UserOperation.Delete, UserSummary.VersionOf(target), true, new(actor.Id, actor.SecurityStamp, null), null, null, Token);
        Assert.False(pending.IsCompleted); // 原账号门固定排队窗口；不替换服务/事务。
        TestAccounts.Update(factory.Services, "admin", user => { user.Role = nameof(UserRole.Viewer); user.SecurityStamp = SecurityStamps.New(); });
        gate.Dispose();
        var error = await Assert.ThrowsAsync<UserManagementException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal("user.actorChanged", error.Code);
        using var remaining = await factory.CreateLoggedInClientAsync("survivor", Token);
        var users = (await remaining.GetJsonAsync(Root, Token)).GetProperty("items");
        Assert.Equal(3, users.GetArrayLength());
        Assert.Contains(users.EnumerateArray(), user => user.GetProperty("accountName").GetString() == "target");
    }

    [Fact]
    public async Task QueuedSelfPasswordChangeCannotReviveARevokedSession()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var target = TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("target", Token);
        var lockout = factory.Services.GetRequiredService<LoginLockout>();
        var accounts = factory.Services.GetRequiredService<AccountService>();
        using var gate = await lockout.EnterAsync(target.NormalizedAccountName, Token);
        var pending = accounts.ChangePasswordAsync(target.Id, target.SecurityStamp, ApiRequests.StrongPassword, ApiRequests.AnotherStrongPassword, null, Token);
        Assert.False(pending.IsCompleted);
        // 等价于管理员已提交强制下线/改角色：旧请求已通过入口认证，但尚未取得原账号门。
        TestAccounts.Update(factory.Services, "target", user => user.SecurityStamp = SecurityStamps.New());
        gate.Dispose();
        Assert.IsType<PasswordChangeOutcome.AccountUnavailable>(await pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
        using var oldSession = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var fresh = await factory.CreateLoggedInClientAsync("target", Token);
        await fresh.GetJsonAsync(ApiRequests.Session, Token);
    }
}

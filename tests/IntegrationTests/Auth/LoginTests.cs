using System.Buffers.Binary;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

/// <summary>
/// 登录、锁定、修改密码与登出（T0-07；功能需求 §1.4、§1.7）。锁定期由注入的可控时钟推进。
/// </summary>
public sealed class LoginTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CorrectPasswordStartsASessionAndRecordsTheLastLogin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(Start);
        using var parent = new TenonEdgeApplicationFactory();
        using var factory = WithTime(parent, time);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = factory.CreateClient();

        using var response = await client.LoginAsync("Operator", ApiRequests.StrongPassword, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.ReadJsonAsync(cancellationToken);
        Assert.Equal("operator", session.GetProperty("accountName").GetString());
        Assert.Equal("configurator", session.GetProperty("role").GetString());
        using (var current = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        var user = TestAccounts.Find(factory.Services, "operator")!;
        Assert.Equal(Start.ToUnixTimeMilliseconds(), user.LastLoginAt);
        Assert.Equal(AuditResults.Success, Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.Login)).Result);
    }

    [Fact]
    public async Task WrongPasswordAndUnknownAccountGetTheSameResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = factory.CreateClient();

        using var wrongPassword = await client.LoginAsync("admin", "Wrong-Password-1", cancellationToken);
        using var unknownAccount = await client.LoginAsync("nobody", "Wrong-Password-1", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAccount.StatusCode);
        var first = await wrongPassword.ReadJsonAsync(cancellationToken);
        var second = await unknownAccount.ReadJsonAsync(cancellationToken);
        Assert.Equal(AuthEndpoints.InvalidCredentialsCode, first.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("title").GetString(), second.GetProperty("title").GetString());
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
        Assert.Equal(2, TestAccounts.Audit(factory.Services, AuditActions.Login).Count(record => record.Result == AuditResults.Failure));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("nobody")]
    public async Task FiveConsecutiveFailuresLockTheAccountNameForFifteenMinutes(string accountName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(Start);
        using var parent = new TenonEdgeApplicationFactory();
        using var factory = WithTime(parent, time);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt < LoginLockout.MaximumFailures; attempt++)
        {
            using var failed = await client.LoginAsync(accountName, "Wrong-Password-1", cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using (var fifth = await client.LoginAsync(accountName, "Wrong-Password-1", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
            Assert.Equal(AuthEndpoints.LockedOutCode, await fifth.ReadProblemCodeAsync(cancellationToken));
            Assert.Equal(TimeSpan.FromMinutes(15), fifth.Headers.RetryAfter?.Delta);
        }

        // 锁定期间即使密码正确也拒绝；对已有账号与不存在的账号提示相同。
        time.Advance(TimeSpan.FromMinutes(14));
        using (var duringLockout = await client.LoginAsync(accountName, ApiRequests.StrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, duringLockout.StatusCode);
            Assert.Equal(TimeSpan.FromMinutes(1), duringLockout.Headers.RetryAfter?.Delta);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        using var afterLockout = await client.LoginAsync(accountName, ApiRequests.StrongPassword, cancellationToken);
        Assert.Equal(accountName == "admin" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, afterLockout.StatusCode);
    }

    [Fact]
    public void PasswordHashUsesPbkdf2WithHmacSha512And100000Iterations()
    {
        // V-11 要求记录密码哈希的格式与迭代次数（S-16）：Identity V3 格式头依次为格式标记、PRF、迭代次数、盐长度（大端）。
        using var factory = new TenonEdgeApplicationFactory();
        var user = TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);

        var hash = Convert.FromBase64String(user.PasswordHash!);

        Assert.Equal(0x01, hash[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1)));
        Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9)));
        Assert.Equal(13 + 16 + 32, hash.Length);
    }

    [Fact]
    public async Task ChangingThePasswordRequiresTheCurrentPasswordAndRetiresTheOldOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        var original = TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = factory.CreateClient();
        using (var login = await client.LoginAsync("viewer", ApiRequests.StrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, cancellationToken);
        }

        using (var wrongCurrent = await client.PostJsonAsync(
            ApiRequests.ChangePassword,
            new { currentPassword = "Not-The-Password-1", newPassword = ApiRequests.AnotherStrongPassword },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
            Assert.True((await wrongCurrent.ReadJsonAsync(cancellationToken)).GetProperty("errors").TryGetProperty("currentPassword", out _));
        }

        using (var unchanged = await client.PostJsonAsync(
            ApiRequests.ChangePassword,
            new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.StrongPassword },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unchanged.StatusCode);
        }

        using (var changed = await client.PostJsonAsync(
            ApiRequests.ChangePassword,
            new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.AnotherStrongPassword },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        using var fresh = factory.CreateClient();
        using (var oldPassword = await fresh.LoginAsync("viewer", ApiRequests.StrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }

        using (var newPassword = await fresh.LoginAsync("viewer", ApiRequests.AnotherStrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
        }

        var updated = TestAccounts.Find(factory.Services, "viewer")!;
        Assert.NotEqual(original.SecurityStamp, updated.SecurityStamp);
        Assert.Contains(TestAccounts.Audit(factory.Services, AuditActions.PasswordChanged), record => record.Result == AuditResults.Success);
    }

    [Fact]
    public async Task TemporaryPasswordMustBeChangedBeforeOtherApisCanBeUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "newhire", ApiRequests.StrongPassword, UserRole.Configurator, mustChangePassword: true);
        using var client = factory.CreateClient();

        using (var login = await client.LoginAsync("newhire", ApiRequests.StrongPassword, cancellationToken))
        {
            Assert.True((await client.UseSessionAsync(login, cancellationToken)).GetProperty("mustChangePassword").GetBoolean());
        }

        using (var session = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        using (var blocked = await client.GetAsync(new Uri(ApiRequests.ApiDocument, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal(PasswordChangeRequiredFilter.Code, await blocked.ReadProblemCodeAsync(cancellationToken));
        }

        using (var changed = await client.PostJsonAsync(
            ApiRequests.ChangePassword,
            new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.AnotherStrongPassword },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.False((await client.UseSessionAsync(changed, cancellationToken)).GetProperty("mustChangePassword").GetBoolean());
        }

        using var allowed = await client.GetAsync(new Uri(ApiRequests.ApiDocument, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task LogoutEndsTheSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = factory.CreateClient();
        using (var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, cancellationToken);
        }

        using (var logout = await client.PostAsync(new Uri(ApiRequests.Logout, UriKind.Relative), content: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using var session = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Contains(TestAccounts.Audit(factory.Services, AuditActions.Logout), record => record.AccountName == "admin");
    }

    [Fact]
    public async Task DisabledAccountCannotLogInAndGetsTheSameResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "retired", ApiRequests.StrongPassword, UserRole.Viewer, enabled: false);
        using var client = factory.CreateClient();

        using var response = await client.LoginAsync("retired", ApiRequests.StrongPassword, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthEndpoints.InvalidCredentialsCode, await response.ReadProblemCodeAsync(cancellationToken));
    }

    [Fact]
    public async Task ConcurrentWrongPasswordsCannotGetPastTheLockoutLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);

        // 进程内的请求没有来源地址，同属一个限流分区；并发数等于每分钟的限额，被拒绝的只能是锁定。
        var clients = Enumerable.Range(0, LoginRateLimiting.PermitLimit).Select(_ => factory.CreateClient()).ToList();
        var responses = await Task.WhenAll(clients.Select(client => client.LoginAsync("admin", "Wrong-Password-1", cancellationToken)));
        try
        {
            Assert.Equal(LoginLockout.MaximumFailures - 1, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
            var rejected = responses.Where(response => response.StatusCode == HttpStatusCode.TooManyRequests).ToList();
            Assert.Equal(LoginRateLimiting.PermitLimit - LoginLockout.MaximumFailures + 1, rejected.Count);
            foreach (var response in rejected)
            {
                Assert.Equal(AuthEndpoints.LockedOutCode, await response.ReadProblemCodeAsync(cancellationToken));
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }

            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        // 只校验了 5 次密码，其余尝试在锁定生效后直接被拒，锁定没有被延长。
        var audit = TestAccounts.Audit(factory.Services, AuditActions.Login);
        Assert.Equal(LoginLockout.MaximumFailures, audit.Count(record => record.Detail!.StartsWith("密码错误", StringComparison.Ordinal)));
        Assert.Equal(LoginRateLimiting.PermitLimit - LoginLockout.MaximumFailures, audit.Count(record => record.Detail == "账号处于锁定期"));
        Assert.Single(audit, record => record.Detail!.EndsWith("锁定 15 分钟", StringComparison.Ordinal));
        var user = TestAccounts.Find(factory.Services, "admin")!;
        Assert.NotNull(user.LockoutEnd);
        Assert.Equal(0, user.FailedLoginCount);
    }

    [Fact]
    public async Task RepeatedWrongCurrentPasswordsEndEverySessionOfTheAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var owner = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        using var thief = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        var guess = new { currentPassword = "Not-The-Password-1", newPassword = ApiRequests.AnotherStrongPassword };

        for (var attempt = 1; attempt < LoginLockout.MaximumFailures; attempt++)
        {
            using var rejected = await thief.PostJsonAsync(ApiRequests.ChangePassword, guess, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        // 拿到会话的人猜原密码，达到上限时安全戳被更换，该账号的全部会话（包括猜测者自己的）随即结束。
        using (var fifth = await thief.PostJsonAsync(ApiRequests.ChangePassword, guess, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);
            Assert.Equal(AuthEndpoints.SessionEndedCode, await fifth.ReadProblemCodeAsync(cancellationToken));
        }

        foreach (var client in new[] { thief, owner })
        {
            using var session = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }

        // 原密码没有被改掉，也没有触发登录锁定：账号的主人可以重新登录。
        using var again = factory.CreateClient();
        using var login = await again.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var audit = TestAccounts.Audit(factory.Services, AuditActions.PasswordChanged);
        Assert.Equal(LoginLockout.MaximumFailures - 1, audit.Count(record => record.Detail == "原密码不正确"));
        Assert.Single(audit, record => record.Detail!.StartsWith("原密码连续错误", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginLockoutDoesNotStopTheOwnerFromChangingThePassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var owner = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        using var stolen = await factory.CreateLoggedInClientAsync("admin", cancellationToken);

        // 网络上的任何人都可以用错误的密码把账号锁住。
        using var anonymous = factory.CreateClient();
        for (var attempt = 1; attempt <= LoginLockout.MaximumFailures; attempt++)
        {
            using var failed = await anonymous.LoginAsync("admin", "Wrong-Password-1", cancellationToken);
        }

        using (var locked = await anonymous.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        }

        // 锁定期间，已登录的主人仍能修改密码，从而结束被窃取的会话。
        using (var changed = await owner.PostJsonAsync(
            ApiRequests.ChangePassword,
            new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.AnotherStrongPassword },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        using var session = await stolen.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task UnknownAccountNamesAreNotWrittenToTheAudit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = factory.CreateClient();

        // 用户可能把密码误输入账号栏；这里的“账号名”本身就是一个符合账号名格式的密码。
        using var response = await client.LoginAsync(ApiRequests.AnotherStrongPassword, ApiRequests.StrongPassword, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var record = Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.Login));
        Assert.Null(record.AccountName);
        Assert.Equal("账号不存在", record.Detail);
        Assert.DoesNotContain(ApiRequests.AnotherStrongPassword, $"{record.AccountName}|{record.Target}|{record.Detail}", StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> WithTime(WebApplicationFactory<Program> factory, TimeProvider time) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(time)));
}

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

/// <summary>
/// 首次设置（T0-07；GW-01、AT-24、FS-09）：窗口与状态限制、比较并交换、自动登录、审计、密码与账号规则。
/// 时间由注入的可控时钟决定，不等待真实的 30 分钟。
/// </summary>
public sealed class SetupTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshGatewayReportsUninitializedStateAndAnOpenWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var parent = new TenonEdgeApplicationFactory();
        using var factory = WithTime(parent, new ManualTimeProvider(Start));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.ReadJsonAsync(cancellationToken);
        Assert.Equal("uninitialized", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("windowOpen").GetBoolean());
        Assert.Equal(Start + SetupWindow.Duration, status.GetProperty("windowClosesAt").GetDateTimeOffset());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, status.GetProperty("resetAccountName").ValueKind);

        // 不存在默认或演示账号（GW-01、AT-01）。
        Assert.Equal(0, factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<UserAccountRecord>().Count());
    }

    [Fact]
    public async Task CompletingSetupCreatesAnAdministratorSignsInAndClosesSetup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(Start);
        using var parent = new TenonEdgeApplicationFactory();
        using var factory = WithTime(parent, time);
        using var client = factory.CreateClient();
        time.Advance(TimeSpan.FromMinutes(5));

        using var response = await client.CompleteSetupAsync(cancellationToken, gatewayName: "一号车间网关");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.ReadJsonAsync(cancellationToken);
        Assert.Equal("admin", session.GetProperty("accountName").GetString());
        Assert.Equal("administrator", session.GetProperty("role").GetString());
        Assert.False(session.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("csrfToken").GetString()));

        // 经 HTTP 签发的会话 Cookie：HTTP 专用名称，HttpOnly、SameSite=Strict，不带 Secure。
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{SessionAuthentication.HttpCookieName}=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);

        using (var current = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        using (var status = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken))
        {
            var body = await status.ReadJsonAsync(cancellationToken);
            Assert.Equal("initialized", body.GetProperty("state").GetString());
            Assert.False(body.GetProperty("windowOpen").GetBoolean());
        }

        var user = TestAccounts.Find(factory.Services, "admin");
        Assert.NotNull(user);
        Assert.Equal(nameof(UserRole.Administrator), user.Role);
        Assert.DoesNotContain(ApiRequests.StrongPassword, user.PasswordHash, StringComparison.Ordinal);
        var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        Assert.Equal(SetupStates.Initialized, state.SetupState);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), state.SetupCompletedAt);
        Assert.Equal("一号车间网关", state.GatewayName);
        var audit = Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.SetupCompleted));
        Assert.Equal(AuditResults.Success, audit.Result);
        Assert.Equal("admin", audit.AccountName);
    }

    [Fact]
    public async Task SecondSubmissionIsRejectedAfterInitialization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        using (var first = await client.CompleteSetupAsync(cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var anonymous = factory.CreateClient();
        using var second = await anonymous.CompleteSetupAsync(cancellationToken, accountName: "intruder");

        await ProblemAssert.HasStatusAsync(second, HttpStatusCode.Conflict, cancellationToken);
        Assert.Null(TestAccounts.Find(factory.Services, "intruder"));
    }

    [Fact]
    public async Task ConcurrentSubmissionsInitializeTheGatewayOnlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        var clients = Enumerable.Range(0, 8).Select(_ => factory.CreateClient()).ToList();
        try
        {
            var responses = await Task.WhenAll(clients.Select((client, index) =>
                client.CompleteSetupAsync(cancellationToken, accountName: $"admin{index}")));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
                var rejected = responses.Where(response => response.StatusCode != HttpStatusCode.OK).ToList();
                Assert.Equal(7, rejected.Count);
                foreach (var response in rejected)
                {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                    Assert.Equal(SetupEndpoints.AlreadyCompletedCode, await response.ReadProblemCodeAsync(cancellationToken));
                }
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            var database = factory.Services.GetRequiredService<ConfigDatabase>();
            Assert.Equal(1, database.Client.Queryable<UserAccountRecord>().Count());
            Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.SetupCompleted));
        }
        finally
        {
            clients.ForEach(client => client.Dispose());
        }
    }

    [Fact]
    public async Task SubmissionAfterTheWindowClosesIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(Start);
        using var parent = new TenonEdgeApplicationFactory();
        using var factory = WithTime(parent, time);
        using var client = factory.CreateClient();
        time.Advance(SetupWindow.Duration + TimeSpan.FromSeconds(1));

        using var status = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken);
        Assert.False((await status.ReadJsonAsync(cancellationToken)).GetProperty("windowOpen").GetBoolean());
        using var response = await client.CompleteSetupAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SetupEndpoints.WindowClosedCode, await response.ReadProblemCodeAsync(cancellationToken));
        Assert.Null(TestAccounts.Find(factory.Services, "admin"));
    }

    [Fact]
    public async Task RestartReopensTheWindowOnlyWhileTheGatewayIsNotInitialized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();

        // 窗口过期后未完成设置：重启服务重新开放。
        var firstStart = new ManualTimeProvider(Start);
        using (var parent = new FixedDataDirectoryApplicationFactory(directory.Path))
        using (var factory = WithTime(parent, firstStart))
        using (var client = factory.CreateClient())
        {
            firstStart.Advance(SetupWindow.Duration + TimeSpan.FromMinutes(1));
            using var status = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken);
            Assert.False((await status.ReadJsonAsync(cancellationToken)).GetProperty("windowOpen").GetBoolean());
        }

        using (var parent = new FixedDataDirectoryApplicationFactory(directory.Path))
        using (var factory = WithTime(parent, new ManualTimeProvider(Start + TimeSpan.FromHours(2))))
        using (var client = factory.CreateClient())
        {
            using var status = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken);
            Assert.True((await status.ReadJsonAsync(cancellationToken)).GetProperty("windowOpen").GetBoolean());
            using var completed = await client.CompleteSetupAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        // 已初始化：重启后不再开放。
        using (var restarted = new FixedDataDirectoryApplicationFactory(directory.Path))
        using (var client = restarted.CreateClient())
        {
            using var status = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken);
            var body = await status.ReadJsonAsync(cancellationToken);
            Assert.Equal("initialized", body.GetProperty("state").GetString());
            Assert.False(body.GetProperty("windowOpen").GetBoolean());
            using var again = await client.CompleteSetupAsync(cancellationToken, accountName: "second");
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
    }

    [Theory]
    [InlineData("12345678", "字母和数字")]
    [InlineData("abcdefgh", "字母和数字")]
    [InlineData("abc123", "至少 8 位")]
    [InlineData("admin123", "过于常见")]
    [InlineData("Password123", "过于常见")]
    [InlineData("admin2026x", null)]
    public async Task WeakPasswordsAreRejectedWithFieldErrors(string password, string? reason)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        var accountName = reason is null ? "admin2026x" : "admin";

        using var response = await client.CompleteSetupAsync(cancellationToken, accountName: accountName, password: password);

        var errors = await ValidationErrorsAsync(response, cancellationToken);
        var message = Assert.Single(Assert.Contains("password", errors));
        Assert.Contains(reason ?? "不能与账号相同", message, StringComparison.Ordinal);
        Assert.Null(TestAccounts.Find(factory.Services, accountName));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("name*with*star")]
    [InlineData("a-very-long-account-name-over-32-chars")]
    public async Task InvalidAccountNamesAreRejectedWithFieldErrors(string accountName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.CompleteSetupAsync(cancellationToken, accountName: accountName);

        var errors = await ValidationErrorsAsync(response, cancellationToken);
        Assert.Contains("accountName", errors);
    }

    [Fact]
    public async Task OverlongGatewayNameIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.CompleteSetupAsync(cancellationToken, gatewayName: new string('网', SetupService.GatewayNameMaximumLength + 1));

        var errors = await ValidationErrorsAsync(response, cancellationToken);
        Assert.Contains("gatewayName", errors);
    }

    private static WebApplicationFactory<Program> WithTime(WebApplicationFactory<Program> factory, TimeProvider time) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(time)));

    private static async Task<Dictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadJsonAsync(cancellationToken);
        return problem.GetProperty("errors").EnumerateObject()
            .ToDictionary(field => field.Name, field => field.Value.EnumerateArray().Select(item => item.GetString()!).ToArray());
    }
}

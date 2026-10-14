using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Audit;

/// <summary>
/// 审计查询与留存（T0-09；功能需求 §9.2，总设计 §3.3）：管理员按时间、账号、操作、对象筛选并从新到旧键集分页；
/// 首次设置、登录与主机侧重置都留有审计，重启后仍可查询；审计写不进配置库时不阻断登录，改写到日志。
/// 其他角色无权查看见 SessionSecurityTests。
/// </summary>
public sealed class AuditQueryTests
{
    private const string AuditPath = "/api/edge-admin/v1/audit";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTokenAuditIdentityIsQueryableAfterRestart(bool transactional)
    {
        var token = TestContext.Current.CancellationToken;
        using var data = TemporaryDirectory.Create();
        await using (var first = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = first.CreateClient();
            using var setup = await client.CompleteSetupAsync(token);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            var audit = first.Services.GetRequiredService<AuditTrail>();
            var entry = new AuditEntry("test.tokenWrite", AuditResults.Success, new AuditAccount("admin", "MES 夜班"),
                "192.0.2.11", "test-object", "non-secret operation");
            if (transactional)
            {
                first.Services.GetRequiredService<ConfigDatabase>().InTransaction(database =>
                { audit.Append(database, entry); return true; });
            }
            else
            { audit.AppendOrLog(entry); }
        }
        await using var second = new FixedDataDirectoryApplicationFactory(data.Path);
        using var reader = second.CreateClient();
        using var login = await reader.LoginAsync("admin", ApiRequests.StrongPassword, token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var page = await reader.GetJsonAsync($"{AuditPath}?action=test.tokenWrite", token);
        var record = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("admin", record.GetProperty("accountName").GetString());
        Assert.Equal("MES 夜班", record.GetProperty("tokenName").GetString());
        Assert.Equal("192.0.2.11", record.GetProperty("sourceIp").GetString());
        Assert.Equal("test-object", record.GetProperty("target").GetString());
    }

    [Fact]
    public void TokenAuditFallbackKeepsTheSameNonSecretIdentity()
    {
        var capture = new LogCapture();
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(capture)));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var audit = factory.Services.GetRequiredService<AuditTrail>();
        database.Client.Ado.ExecuteCommand($"ALTER TABLE {AuditLogRecord.TableName} RENAME TO audit_log_unavailable");
        try
        { audit.AppendOrLog(new("test.tokenWrite", AuditResults.Success, new AuditAccount("admin", "MES 夜班"), "192.0.2.11")); }
        finally
        { database.Client.Ado.ExecuteCommand($"ALTER TABLE audit_log_unavailable RENAME TO {AuditLogRecord.TableName}"); }
        var fallback = Assert.Single(capture.Matching(typeof(AuditTrail).FullName!, "审计记录写入配置库失败，改写到日志"));
        Assert.Contains("令牌 MES 夜班", fallback.Message, StringComparison.Ordinal);
        Assert.Contains("账号 admin，来源 192.0.2.11", fallback.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministratorsCanFilterAndPageThroughAuditRecordsNewestFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var seeded = Seed(
            factory.Services,
            ("2026-09-01T00:00:00Z", "alice", AuditActions.Login, null),
            ("2026-09-02T00:00:00Z", "bob", AuditActions.Login, null),
            ("2026-09-03T00:00:00Z", "alice", AuditActions.PasswordChanged, "alice"),
            ("2026-09-04T00:00:00Z", "alice", AuditActions.Login, null),
            ("2026-09-05T00:00:00Z", "bob", AuditActions.Logout, null));
        using var client = await factory.CreateLoggedInClientAsync("admin", cancellationToken);

        var first = await client.GetJsonAsync($"{AuditPath}?account=alice&limit=2", cancellationToken);
        Assert.Equal([seeded[3], seeded[2]], Ids(first));
        var cursor = first.GetProperty("nextCursor").GetInt64();
        Assert.Equal(seeded[2], cursor);

        var second = await client.GetJsonAsync($"{AuditPath}?account=alice&limit=2&before={cursor}", cancellationToken);
        Assert.Equal([seeded[0]], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        var window = await client.GetJsonAsync(
            $"{AuditPath}?action={AuditActions.Login}&from=2026-09-02T00:00:00Z&to=2026-09-04T00:00:00Z",
            cancellationToken);
        Assert.Equal([seeded[1]], Ids(window));

        var byTarget = await client.GetJsonAsync($"{AuditPath}?target=alice", cancellationToken);
        var record = Assert.Single(byTarget.GetProperty("items").EnumerateArray());
        Assert.Equal(AuditActions.PasswordChanged, record.GetProperty("action").GetString());
        Assert.Equal("2026-09-03T00:00:00+00:00", record.GetProperty("occurredAt").GetString());
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("before=0")]
    [InlineData("before=9007199254740992")]
    [InlineData("from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-01-01T00:00:00Z")]
    public async Task PageSizeOutsideTheAllowedRangeIsAValidationProblem(string query)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", cancellationToken);

        using var response = await client.GetAsync(new Uri($"{AuditPath}?{query}", UriKind.Relative), cancellationToken);

        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.BadRequest, cancellationToken);
    }

    [Fact]
    public async Task SetupLoginAndHostResetAreAuditedAndStillQueryableAfterRestarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var dataDirectory = TemporaryDirectory.Create();

        await using (var first = new FixedDataDirectoryApplicationFactory(dataDirectory.Path))
        {
            using var client = first.CreateClient();
            using var setup = await client.CompleteSetupAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            using var wrong = await client.LoginAsync("admin", "Wrong-Password-1", cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.Equal(
            HostExitCodes.Success,
            await MaintenanceCommands.RunAsync(["reset-admin", $"--{DataDirectory.ConfigurationKey}", dataDirectory.Path]));

        await using var second = new FixedDataDirectoryApplicationFactory(dataDirectory.Path);
        using (var client = second.CreateClient())
        using (var setup = await client.CompleteSetupAsync(cancellationToken, password: ApiRequests.AnotherStrongPassword))
        {
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        }

        using var administrator = second.CreateClient();
        using (var login = await administrator.LoginAsync("admin", ApiRequests.AnotherStrongPassword, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        // 主机侧重置由主机上的操作人员执行，记录中没有网页账号，对象为被重置的账号。
        var page = await administrator.GetJsonAsync(AuditPath, cancellationToken);
        var actions = page.GetProperty("items").EnumerateArray()
            .Select(item => $"{item.GetProperty("action").GetString()} {item.GetProperty("result").GetString()}")
            .Reverse()
            .ToList();
        Assert.Equal(
            [
                "setup.complete success",
                "auth.login failure",
                "host.resetAdministrator success",
                "setup.complete success",
                "auth.login success",
            ],
            actions);
    }

    [Fact]
    public async Task LoginSucceedsAndTheAuditRecordGoesToTheLogWhenTheAuditTableCannotBeWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var capture = new LogCapture();
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(capture)));
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"ALTER TABLE {AuditLogRecord.TableName} RENAME TO audit_log_unavailable");
        try
        {
            using var client = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
            using var session = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }
        finally
        {
            database.Client.Ado.ExecuteCommand($"ALTER TABLE audit_log_unavailable RENAME TO {AuditLogRecord.TableName}");
        }

        var fallback = Assert.Single(capture.Matching(typeof(AuditTrail).FullName!, "审计记录写入配置库失败，改写到日志"));
        Assert.Contains("操作 auth.login，结果 success，账号 admin", fallback.Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, fallback.Level);
    }

    private static List<long> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())];

    /// <summary>按给定时间直接写入审计记录（时间无法经 API 指定），返回按写入顺序的 ID。</summary>
    private static List<long> Seed(IServiceProvider services, params (string OccurredAt, string Account, string Action, string? Target)[] records)
    {
        var ids = services.GetRequiredService<SnowflakeIdGenerator>();
        var rows = records.Select(record => new AuditLogRecord
        {
            Id = ids.NextId(),
            OccurredAt = DateTimeOffset.Parse(record.OccurredAt, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            AccountName = record.Account,
            SourceIp = "192.0.2.10",
            Action = record.Action,
            Target = record.Target,
            Result = AuditResults.Success,
        }).ToList();
        services.GetRequiredService<ConfigDatabase>().Client.Insertable(rows).ExecuteCommand();
        return [.. rows.Select(row => row.Id)];
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

/// <summary>
/// 运行事件查询与 T0 的事件生产者（T0-09；总设计 §3.3、§10.1、§10.3）：所有角色可查看，未登录 401；
/// 服务启停、首次设置窗口开放、运行时启动条件不满足都记为运行事件；按时间、级别、类型、对象筛选并键集分页（功能需求 §7.4）。
/// </summary>
public sealed class RuntimeEventEndpointTests
{
    private const string EventsPath = "/api/edge-admin/v1/runtime/events";

    [Fact]
    public async Task EveryRoleCanReadRuntimeEventsAndAnonymousCannot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);

        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.GetAsync(new Uri(EventsPath, UriKind.Relative), cancellationToken))
        {
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, cancellationToken);
        }

        foreach (var accountName in new[] { "admin", "operator", "viewer" })
        {
            using var client = await factory.CreateLoggedInClientAsync(accountName, cancellationToken);
            var page = await client.GetJsonAsync(EventsPath, cancellationToken);

            // 新数据目录：宿主启动时首次设置窗口开放，随后服务启动完成。
            var events = Items(page).Select(item => (item.GetProperty("type").GetString(), item.GetProperty("level").GetString())).ToList();
            Assert.Contains((RuntimeEventTypes.SetupWindowOpen, "warning"), events);
            Assert.Contains((RuntimeEventTypes.ServiceStarted, "info"), events);
        }
    }

    [Fact]
    public async Task EventsCanBeFilteredAndPagedNewestFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        var runtimeEvents = factory.Services.GetRequiredService<RuntimeEventLog>();
        foreach (var target in new[] { "t1", "t2", "t3", "t4", "t5" })
        {
            runtimeEvents.Record(RuntimeEventLevel.Warning, "test.flap", target, $"{target} 断开");
        }

        runtimeEvents.Record(RuntimeEventLevel.Warning, "test.flap", "t3", "t3 再次断开");
        runtimeEvents.Record(RuntimeEventLevel.Error, "test.flap", "t3", "t3 失败");
        using var client = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        var first = await client.GetJsonAsync($"{EventsPath}?type=test.flap&level=warning&limit=2", cancellationToken);
        Assert.Equal(["t5", "t4"], Targets(first));
        var second = await client.GetJsonAsync(
            $"{EventsPath}?type=test.flap&level=warning&limit=2&before={first.GetProperty("nextCursor").GetInt64()}",
            cancellationToken);
        Assert.Equal(["t3", "t2"], Targets(second));
        var third = await client.GetJsonAsync(
            $"{EventsPath}?type=test.flap&level=warning&limit=2&before={second.GetProperty("nextCursor").GetInt64()}",
            cancellationToken);
        Assert.Equal(["t1"], Targets(third));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextCursor").ValueKind);

        var merged = Assert.Single(Items(await client.GetJsonAsync($"{EventsPath}?type=test.flap&target=t3&level=warning", cancellationToken)));
        Assert.Equal(2, merged.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal("t3 再次断开", merged.GetProperty("message").GetString());
        var error = Assert.Single(Items(await client.GetJsonAsync($"{EventsPath}?level=error", cancellationToken)));
        Assert.Equal("t3 失败", error.GetProperty("message").GetString());

        var service = Assert.Single(Items(await client.GetJsonAsync($"{EventsPath}?type={RuntimeEventTypes.ServiceStarted}", cancellationToken)));
        Assert.Equal(JsonValueKind.Null, service.GetProperty("target").ValueKind);
    }

    [Fact]
    public async Task EventsCanBeFilteredByWhenTheyOccurred()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        var ids = factory.Services.GetRequiredService<SnowflakeIdGenerator>();
        var day = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var records = new[] { ("early", 8.0, 8.5), ("spanning", 8.75, 9.5), ("late", 10.0, 10.0) }
            .Select(item => new RuntimeEventRecord
            {
                Id = ids.NextId(),
                FirstOccurredAt = day.AddHours(item.Item2).ToUnixTimeMilliseconds(),
                LastOccurredAt = day.AddHours(item.Item3).ToUnixTimeMilliseconds(),
                Level = "warning",
                Type = "test.window",
                Target = item.Item1,
                Message = item.Item1,
            })
            .ToList();
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Insertable(records).ExecuteCommand();
        using var client = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        // 从首次到最近一次发生的区间与查询区间 [from, to) 相交即列出。
        Assert.Equal(
            ["spanning"],
            Targets(await client.GetJsonAsync($"{EventsPath}?type=test.window&from=2026-09-01T09:00:00Z&to=2026-09-01T10:00:00Z", cancellationToken)));
        Assert.Equal(
            ["late", "spanning", "early"],
            Targets(await client.GetJsonAsync($"{EventsPath}?type=test.window&from=2026-09-01T08:30:00Z", cancellationToken)));
        Assert.Equal(
            ["early"],
            Targets(await client.GetJsonAsync($"{EventsPath}?type=test.window&to=2026-09-01T08:45:00Z", cancellationToken)));
    }

    [Theory]
    [InlineData("level=fatal")]
    [InlineData("level=7")]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    public async Task InvalidQueriesAreRejected(string query)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        using var response = await client.GetAsync(new Uri($"{EventsPath}?{query}", UriKind.Relative), cancellationToken);

        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.BadRequest, cancellationToken);
    }

    [Fact]
    public async Task ServiceStopAndRestartAreRecordedAndStartsWithinFiveMinutesAreMerged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var dataDirectory = TemporaryDirectory.Create();
        await using (var first = new FixedDataDirectoryApplicationFactory(dataDirectory.Path))
        {
            TestAccounts.Create(first.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        }

        await using var second = new FixedDataDirectoryApplicationFactory(dataDirectory.Path);
        using var client = await second.CreateLoggedInClientAsync("viewer", cancellationToken);

        var stopping = Assert.Single(Items(await client.GetJsonAsync($"{EventsPath}?type={RuntimeEventTypes.ServiceStopping}", cancellationToken)));
        Assert.Equal(1, stopping.GetProperty("occurrenceCount").GetInt32());
        var started = Assert.Single(Items(await client.GetJsonAsync($"{EventsPath}?type={RuntimeEventTypes.ServiceStarted}", cancellationToken)));
        Assert.Equal(2, started.GetProperty("occurrenceCount").GetInt32());
        Assert.Contains(ProductVersion.Current, started.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeStartBlockersAreRecordedAsErrors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var blocked = new RuntimeStartConditions();
        blocked.Block("test-blocker", "测试登记的阻止原因");
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(blocked)));
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        var page = await client.GetJsonAsync($"{EventsPath}?type={RuntimeEventTypes.RuntimeBlocked}", cancellationToken);

        // 在容器中运行测试时还会有数据目录不在挂载卷上的那一条，这里只核对测试登记的这一条。
        var item = Assert.Single(Items(page), item => item.GetProperty("target").GetString() == "test-blocker");
        Assert.Equal(("error", "test-blocker", "测试登记的阻止原因"), (
            item.GetProperty("level").GetString(),
            item.GetProperty("target").GetString(),
            item.GetProperty("message").GetString()));
    }

    private static List<JsonElement> Items(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private static List<string?> Targets(JsonElement page) => [.. Items(page).Select(item => item.GetProperty("target").GetString())];
}

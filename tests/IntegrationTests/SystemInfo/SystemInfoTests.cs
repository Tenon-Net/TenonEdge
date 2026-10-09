using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.SystemInfo;

/// <summary>
/// 系统信息（T0-09；功能需求 §10.1，总设计 §2.3、§10.3，V-07）：所有角色可查看，未登录 401；
/// 返回版本、NodeId、网关名称、数据目录与磁盘空间、证书指纹与到期时间、实际加载的 SQLite 版本、
/// 首次设置的时间与来源 IP，以及阻止运行时启动的原因。
/// </summary>
public sealed class SystemInfoTests
{
    private const string InfoPath = "/api/edge-admin/v1/system/info";

    [Theory]
    [InlineData(ClockSyncState.Unknown, "unknown")]
    [InlineData(ClockSyncState.Synchronized, "synchronized")]
    [InlineData(ClockSyncState.NotSynchronized, "notSynchronized")]
    public async Task SystemInfoExposesClockState(ClockSyncState state, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IClockSyncStatus>(new FixedClock(state))));
        TestAccounts.Create(configured.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = configured.CreateClient();
        using var login = await client.LoginAsync("viewer", ApiRequests.StrongPassword, cancellationToken);
        await client.UseSessionAsync(login, cancellationToken);
        var info = await client.GetJsonAsync(InfoPath, cancellationToken);
        Assert.Equal(expected, info.GetProperty("clockSyncState").GetString());
    }

    private sealed class FixedClock(ClockSyncState state) : IClockSyncStatus
    {
        public ClockSyncState State => state;
    }

    [Fact]
    public async Task EveryRoleCanReadSystemInfoAndAnonymousCannot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);

        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.GetAsync(new Uri(InfoPath, UriKind.Relative), cancellationToken))
        {
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, cancellationToken);
        }

        foreach (var accountName in new[] { "admin", "operator", "viewer" })
        {
            using var client = await factory.CreateLoggedInClientAsync(accountName, cancellationToken);
            var info = await client.GetJsonAsync(InfoPath, cancellationToken);
            Assert.Equal(ProductVersion.Current, info.GetProperty("version").GetString());
            var runtime = await client.GetJsonAsync("/api/edge-admin/v1/runtime/status", cancellationToken);
            Assert.True(runtime.TryGetProperty("storageProtection", out _));
            Assert.True(runtime.TryGetProperty("persistentCounters", out _));
            Assert.True(runtime.TryGetProperty("unpersistedCounters", out _));
            var trends = await client.GetJsonAsync("/api/edge-admin/v1/runtime/trends", cancellationToken);
            Assert.Equal(JsonValueKind.Array, trends.ValueKind);
        }
    }

    [Fact]
    public async Task SystemInfoReportsIdentityStorageCertificateSqliteAndFirstSetup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        using (var setup = await client.CompleteSetupAsync(cancellationToken, gatewayName: "一号车间网关"))
        {
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            await client.UseSessionAsync(setup, cancellationToken);
        }

        var info = await client.GetJsonAsync(InfoPath, cancellationToken);

        var services = factory.Services;
        Assert.Equal(services.GetRequiredService<GatewayIdentity>().NodeId, info.GetProperty("nodeId").GetString());
        Assert.Equal("一号车间网关", info.GetProperty("gatewayName").GetString());
        Assert.Equal(services.GetRequiredService<DataDirectoryLock>().Directory.Root, info.GetProperty("dataDirectory").GetString());

        var disk = info.GetProperty("dataDirectoryDisk");
        var total = disk.GetProperty("totalBytes").GetInt64();
        var available = disk.GetProperty("availableBytes").GetInt64();
        Assert.InRange(available, 1, total);

        var certificate = services.GetRequiredService<HttpsCertificateStore>().Certificate;
        var reported = info.GetProperty("httpsCertificate");
        Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), reported.GetProperty("sha256Fingerprint").GetString());
        Assert.Equal(
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            reported.GetProperty("notAfter").GetDateTimeOffset());

        var configStore = info.GetProperty("configStore");
        Assert.Equal(ConfigMigrations.All[^1].Version, configStore.GetProperty("schemaVersion").GetInt32());
        var sqliteVersion = configStore.GetProperty("sqliteVersion").GetString();
        Assert.Equal(
            Convert.ToString(services.GetRequiredService<ConfigDatabase>().Client.Ado.GetScalar("SELECT sqlite_version()"), CultureInfo.InvariantCulture),
            sqliteVersion);
        Assert.True(SqliteRuntimeInfo.IsSupportedVersion(sqliteVersion!));
        Assert.NotEmpty(configStore.GetProperty("sqliteCompileOptions").EnumerateArray());

        var state = services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        var firstSetup = info.GetProperty("firstSetup");
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(state.SetupCompletedAt!.Value), firstSetup.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(state.SetupSourceIp, firstSetup.GetProperty("sourceIp").GetString());

        // 返回的是宿主实际登记的阻止原因（在容器中运行测试时，临时数据目录不在挂载卷上，会有一条）。
        Assert.Equal(
            services.GetRequiredService<RuntimeStartConditions>().Blockers.Select(blocker => blocker.Code),
            info.GetProperty("runtimeStartBlockers").EnumerateArray().Select(blocker => blocker.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task FirstSetupIsEmptyUntilSetupHasBeenCompletedThroughTheSetupPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        var info = await client.GetJsonAsync(InfoPath, cancellationToken);

        Assert.Equal(JsonValueKind.Null, info.GetProperty("firstSetup").ValueKind);
        Assert.Equal(JsonValueKind.Null, info.GetProperty("gatewayName").ValueKind);
    }

    [Fact]
    public async Task RuntimeStartBlockersAreShown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var blocked = new RuntimeStartConditions();
        blocked.Block("test-blocker", "测试登记的阻止原因");
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(blocked)));
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        var info = await client.GetJsonAsync(InfoPath, cancellationToken);

        var blocker = Assert.Single(
            info.GetProperty("runtimeStartBlockers").EnumerateArray(),
            blocker => blocker.GetProperty("code").GetString() == "test-blocker");
        Assert.Equal("测试登记的阻止原因", blocker.GetProperty("reason").GetString());
    }
}

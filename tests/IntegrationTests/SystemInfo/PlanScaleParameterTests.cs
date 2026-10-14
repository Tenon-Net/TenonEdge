using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.SystemInfo;

public sealed class PlanScaleParameterTests
{
    private const string Endpoint = "/api/edge-admin/v1/system/runtime-parameters";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("PlanSources", 0)]
    [InlineData("PlanSources", 2001)]
    [InlineData("PlanConnections", 0)]
    [InlineData("PlanConnections", 501)]
    [InlineData("PlanModbusConnections", 0)]
    [InlineData("PlanModbusConnections", 257)]
    [InlineData("PlanPathsPerSource", 0)]
    [InlineData("PlanPathsPerSource", 33)]
    [InlineData("PlanTotalPaths", 0)]
    [InlineData("PlanTotalPaths", 20001)]
    public async Task RangeAndRequiredFieldsAreEnforcedWithoutPersisting(string name, int invalid)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var api = await factory.CreateLoggedInClientAsync("admin", Token);
        var values = JsonSerializer.SerializeToNode(new RuntimeParameterValues(180, 4, 2))!;
        values[name] = invalid;
        using var outOfRange = await api.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { values, expectedVersion = (string?)null, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        values.AsObject().Remove(name);
        using var missing = await api.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { values, expectedVersion = (string?)null, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, "system.runtimeParametersChanged"));
        Assert.Null(factory.Services.GetRequiredService<RuntimeParameterStore>().Read().Version);
    }

    [Fact]
    public async Task PolicyImmediatelyChecksCompileReviewFreezeAndCommitButKeepsActiveAndHistoricalPlans()
    {
        using var data = TemporaryDirectory.Create();
        string version;
        string content;
        string hash;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var api = await factory.CreateLoggedInClientAsync("admin", Token);
            var routes = factory.Services.GetRequiredService<RouteDraftStore>();
            version = CreateDraft(factory.Services);
            var document = RoutePlanCompiler.Compile(routes.ReadCompilation(version, Token), Token).Document;
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            var runtime = factory.Services.GetRequiredService<RuntimeCoordinator>();
            var first = await runtime.ApplyDraftAsync(version, "admin", null, Token);
            Assert.True(first.Activated && first.Committed);
            content = versions.Load(1)!.Content;
            hash = versions.Load(1)!.ContentHash;
            var pending = versions.FreezeDraft(document, version, "admin");
            Assert.Equal(2, pending);
            var low = new RuntimeParameterValues(180, 4, 2, PlanSources: 1, PlanConnections: 1, PlanModbusConnections: 1, PlanPathsPerSource: 1, PlanTotalPaths: 1);
            await Write(api, low);
            Assert.Equal(1, runtime.Runtime.ActiveRevision);
            Assert.Equal(version, routes.Read().Version);
            Assert.Equal(content, versions.Load(1)!.Content);
            using var compile = await api.PostJsonAsync("/api/edge-admin/v1/routes/compile", new { expectedVersion = version }, Token);
            Assert.Equal(HttpStatusCode.OK, compile.StatusCode);
            var rejected = await compile.ReadJsonAsync(Token);
            Assert.False(rejected.GetProperty("compiled").GetBoolean());
            Assert.Equal("sources.totalLimit", rejected.GetProperty("issues")[0].GetProperty("code").GetString());
            Assert.False(versions.ReviewDraft(version, Token).PreflightPassed);
            Assert.Throws<RouteTopologyException>(() => versions.Commit(pending, new(AuditActions.SnapshotApplied, AuditResults.Success, "admin", null), markRunning: true));
            Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
            Assert.Equal(2, versions.ReadPointers().PendingRevision);
            Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.SnapshotApplied));
            versions.Abandon(pending, "fixture: new limits rejected pending plan");
            Assert.Throws<RouteTopologyException>(() => versions.FreezeDraft(document, version, "admin"));
            Assert.Equal(2, versions.ReadHistory().Length);
            Assert.Null(versions.ReadPointers().PendingRevision);
            // 自动保存/历史读取不清空或重算快照；回滚按已校验的历史内容，不伪装成新草稿。
            Assert.True(versions.ReviewRollback(1, version, Token).PreflightPassed);
            var rollback = await runtime.RollbackAsync(1, version, "admin", null, Token);
            Assert.True(rollback.Activated && rollback.Committed);
            Assert.Equal(3, rollback.Revision);
            Assert.Equal(content, versions.Load(3)!.Content);
            version = routes.Read().Version; // 原人工回滚可能推进草稿版本，配置内容不改变。
            await Write(api, new(180, 4, 2));
            Assert.True(versions.ReviewDraft(version, Token).PreflightPassed);
            await Write(api, low);
            Assert.Equal(3, runtime.Runtime.ActiveRevision);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var again = await restarted.CreateLoggedInClientAsync("admin", Token);
        var restored = restarted.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(3, restarted.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
        Assert.Equal(hash, restored.Load(3)!.ContentHash);
        Assert.Equal(content, restored.Load(3)!.Content);
        Assert.Equal(version, restarted.Services.GetRequiredService<RouteDraftStore>().Read().Version);
        Assert.False(restored.ReviewDraft(version, Token).PreflightPassed);
        Assert.Equal(1, restarted.Services.GetRequiredService<RuntimeParameterStore>().Read().Values.PlanSources);
        Assert.Equal(3, restored.ReadHistory().Length);
        Assert.Single(TestAccounts.Audit(restarted.Services, AuditActions.SnapshotRolledBack));
    }

    [Fact]
    public void MigrationFrom28KeepsOldParameterVersionAndPopulatesExistingSafetyDefaults()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(m => m.Version <= 28).ToArray());
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO runtime_parameters(id,version,audit_retention_days,preview_global_limit,preview_session_limit,input_channel_capacity,updated_at) VALUES(1,'11111111111111111111111111111111',365,8,3,7,1)");
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        var row = store.Database.Client.Queryable<RuntimeParameterRecord>().InSingle(1);
        Assert.Equal("11111111111111111111111111111111", row.Version);
        Assert.Equal(7, row.InputChannelCapacity);
        Assert.Equal(365, row.AuditRetentionDays);
        using var client = store.Database.CreateIsolatedClient();
        Assert.Equal(RouteCompilationLimits.Default, RuntimeParameterStore.ReadPlanLimits(client));
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out _));
    }

    private static async Task Write(HttpClient api, RuntimeParameterValues values)
    {
        var current = (await api.GetFromJsonAsync<RuntimeParameterState>(Endpoint, Token))!;
        using var response = await api.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new RuntimeParameterRequest(values, current.Version, true), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string CreateDraft(IServiceProvider services)
    {
        var devices = services.GetRequiredService<DeviceDraftStore>();
        var inputs = services.GetRequiredService<InputDraftStore>();
        var version = "0";
        var ids = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            var device = devices.Write(null, new(version, $"meter{i}", "Meter", DeviceKind.Simulator), "admin", null);
            var input = inputs.Write(device.Device.Id, null, new(device.Version, "sample", "Sample", Simulator: new(PeriodMs: 60000, SampleJson: "{\"value\":42}")), "admin", null);
            version = input.Version;
            ids.Add(input.Input.Id);
        }
        // 连接不在出站例外内，正式输出不能访问外部；本测试不冒充HTTP接收验收。
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(version, "http", "HTTP", ConnectionKind.Http, Http: new("http://127.0.0.1:1/")), "admin", null);
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = ids })),
             new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("in", "out")], new([]));
        return services.GetRequiredService<RouteDraftStore>().Write(null, new(connection.Version, "route", "Route", true, graph), "admin", null).Version;
    }
}

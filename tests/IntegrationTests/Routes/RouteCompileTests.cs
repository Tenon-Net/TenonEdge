using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteCompileTests
{
    private const string Endpoint = "/api/edge-admin/v1/routes/compile";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task CompilationUsesTheSharedDraftVersionAndHasNoRuntimeOrQueueSideEffects(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = Seed(factory);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, new RouteCompileRequest(saved.Version), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = await response.ReadJsonAsync(Token);
            Assert.True(result.GetProperty("compiled").GetBoolean());
            Assert.Equal(1, result.GetProperty("sources").GetInt32());
            Assert.Equal(2, result.GetProperty("paths").GetInt32());
            Assert.Equal(3, result.GetProperty("units").GetInt32());
            Assert.Empty(result.GetProperty("issues").EnumerateArray());
            Assert.StartsWith("sha256:", result.GetProperty("contentHash").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(saved.Version, factory.Services.GetRequiredService<RouteDraftStore>().Read().Version);
        var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PreviousRevision);
        Assert.Null(state.PendingRevision);
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task AnonymousCsrfAndStaleRequestsAreRejectedWhileGraphIssuesHaveLocations()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = Seed(factory);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, new RouteCompileRequest(saved.Version), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var stale = await admin.PostJsonAsync(Endpoint, new RouteCompileRequest("0"), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var graph = saved.Route.Graph;
        graph.Nodes[0] = graph.Nodes[0] with { Config = JsonSerializer.SerializeToElement(new { sourceIds = new long[] { 1 } }) };
        var changed = store.Write(saved.Route.Id, new(saved.Version, saved.Route.Key, saved.Route.Name, true, graph), "test", null);
        using var invalid = await admin.PostJsonAsync(Endpoint, new RouteCompileRequest(changed.Version), Token);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        var result = await invalid.ReadJsonAsync(Token);
        Assert.False(result.GetProperty("compiled").GetBoolean());
        var issue = Assert.Single(result.GetProperty("issues").EnumerateArray());
        Assert.Equal("source.missing", issue.GetProperty("code").GetString());
        Assert.Equal("in", issue.GetProperty("nodeId").GetString());
        Assert.Equal("route", issue.GetProperty("routeKey").GetString());
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PostJsonAsync(Endpoint, new RouteCompileRequest(changed.Version), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task SnapshotIsDetachedAndOldVersionCannotReadAChangingDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = Seed(factory);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var snapshot = store.ReadCompilation(saved.Version, Token);
        store.Write(saved.Route.Id, new(saved.Version, saved.Route.Key, saved.Route.Name, false, saved.Route.Graph), "test", null);
        Assert.Throws<DraftWriteException>(() => store.ReadCompilation(saved.Version, Token));
        Assert.Equal(2, RoutePlanCompiler.Compile(snapshot, Token).Topology.Paths.Length);
        var stopped = RoutePlanCompiler.Compile(store.ReadCompilation(store.Read().Version, Token), Token);
        Assert.Empty(stopped.Topology.Paths);
        Assert.True(Assert.Single(stopped.Document.Units).Configuration.GetProperty("queueOnly").GetBoolean());
    }

    [Fact]
    public async Task SavedGraphCompilesToRealRuntimeUnitsAndRejoinedPathsFreezeSeparateMappedMessages()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = Seed(factory);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var compiled = RoutePlanCompiler.Compile(store.ReadCompilation(saved.Version, Token), Token);
        var connection = Assert.Single(factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections);
        using var directory = TemporaryDirectory.Create();
        var connector = new DeniedConnector();
        await using var runtime = RuntimeTesting.CreateRuntime([new SimulatorInputFactory()], directory.Path,
            outputs: [new HttpOutputFactory(new Anonymous(), TimeProvider.System, connector, new SharedConnections())]);
        await using var plan = runtime.Prepare(compiled.Document.ToPlan(1));
        Assert.Empty(plan.Failures);
        Assert.Equal(0, connector.Attempts);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
        await runtime.StopAsync(Token);
        var records = runtime.Deliveries.ReadPending(connection.Id, 0, 10);
        Assert.Equal(2, records.Count);
        Assert.Single(records.Select(record => record.SourceMessageId).Distinct());
        var values = records.Select(record =>
        {
            using var json = JsonDocument.Parse(record.Payload);
            return json.RootElement.GetProperty("values").GetProperty("n").GetInt64();
        }).Order().ToArray();
        Assert.Equal([84L, 126L], values);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task SnapshotSizeIsCheckedInSqlBeforeMaterializingOversizedSourceContent()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = Seed(factory);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        // 夹具故意损坏已引用来源；应在 JSON 解码前命中总字节预算，而非读取巨大对象再失败。
        database.Client.Ado.ExecuteCommand("UPDATE input_draft SET content = printf('%.*c', 17000000, 'x')");
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        Assert.Equal("plan.snapshotSize", Assert.Throws<RouteTopologyException>(() => store.ReadCompilation(saved.Version, Token)).Issue.Code);
    }

    [Fact]
    public async Task UnreferencedConnectionsParticipateInFullCompilationAndReviewButNotGraphPreview()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = Seed(factory);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var extra = connections.Write(null, new(saved.Version, "unrouted", "Unrouted", ConnectionKind.Mqtt, Mqtt: new("broker.test")), "test", null);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var full = RoutePlanCompiler.Compile(store.ReadCompilation(extra.Version, Token), Token);
        var queue = Assert.Single(full.Document.Units, unit => unit.Id.Kind == "output.mqtt");
        Assert.True(queue.Configuration.GetProperty("queueOnly").GetBoolean());
        Assert.Equal(2, full.Topology.Paths.Length);
        Assert.Equal(full.Document.ContentHash, factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(extra.Version, Token).PlanHash);
        Assert.DoesNotContain(extra.Connection.Id, store.ReadGraphCompilation(extra.Version, saved.Route.Graph, Token).Connections.Keys);
        // 未引用内容也先在SQL里核对字节预算，不能先反序列化巨大JSON。单图预览仍隔离无关内容。
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            "UPDATE connection_draft SET content = printf('%.*c', 17000000, 'x') WHERE business_key = 'unrouted'");
        Assert.Equal("plan.snapshotSize", Assert.Throws<RouteTopologyException>(() => store.ReadCompilation(extra.Version, Token)).Issue.Code);
        Assert.Equal(2, RoutePlanCompiler.Compile(store.ReadGraphCompilation(extra.Version, saved.Route.Graph, Token), Token).Topology.Paths.Length);
    }

    [Fact]
    public async Task TooManyUnreferencedConnectionsAreRejectedBeforeDecoding()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<501)
            INSERT INTO connection_draft(id,business_key,content) SELECT x, 'c'||x, 'invalid' FROM n
            """);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        Assert.Equal("plan.configuration", Assert.Throws<RouteTopologyException>(() => store.ReadCompilation("0", Token)).Issue.Code);
    }

    private static RouteDraftResult Seed(TenonEdgeApplicationFactory factory)
    {
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "test", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"value\":42}")), "test", null);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(input.Version, "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "test", null);
        RouteGraphNode Map(string id, int scale) => new(id, "transform.map", 1, id,
            JsonSerializer.SerializeToElement(new { rules = new[] { new { target = "n", type = "integer", sourceField = "value", scale } } }));
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             Map("a", 2), Map("b", 3), new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out")], new([]));
        return factory.Services.GetRequiredService<RouteDraftStore>().Write(null, new(connection.Version, "route", "Route", true, graph), "test", null);
    }
    private sealed class Anonymous : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private sealed class DeniedConnector : IOutboundConnector
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        { Interlocked.Increment(ref _attempts); throw new OutboundAccessDeniedException(); }
    }
}

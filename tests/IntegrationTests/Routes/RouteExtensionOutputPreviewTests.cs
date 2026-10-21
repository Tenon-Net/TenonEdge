using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteExtensionOutputPreviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [Theory]
    [InlineData("Configurator", false)]
    [InlineData("Configurator", true)]
    [InlineData("Administrator", false)]
    [InlineData("Administrator", true)]
    public async Task PureExtensionPreviewUsesActualFormatterWithoutBuildingSendingOrEnqueuing(string role, bool decorated)
    {
        using var data = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var credentials = new ForbiddenCredentials();
        await using var host = CreateHost(data.Path, credentials);
        var request = Seed(host.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        if (decorated)
        {
            transport.PrefixUnitKind = true;
            transport.ContentTransform = content => content with
            {
                Payload = Encoding.UTF8.GetBytes("wire-format:" + Encoding.UTF8.GetString(content.Payload.Span)),
                Headers = new Dictionary<string, string> { ["X-Wire"] = "original formatter" },
            };
        }
        TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("editor", Token);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview",
            request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadJsonAsync(Token);
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, transport.Builds);
        Assert.Equal(0, transport.Adapters);
        Assert.Equal(0, transport.Sessions);
        Assert.Equal(0, transport.QueueBindings);
        Assert.False(listener.Pending());
        Assert.Equal(0, host.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Equal(request.ExpectedVersion, host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.True(result.GetProperty("previewed").GetBoolean());
        var path = Assert.Single(result.GetProperty("result").GetProperty("paths").EnumerateArray());
        Assert.True(path.GetProperty("succeeded").GetBoolean(), path.GetRawText());
        var output = path.GetProperty("output");
        Assert.Equal("output.wire", output.GetProperty("kind").GetString());
        Assert.Equal("events/sensor", output.GetProperty("address").GetString());
        Assert.Contains("\"scaled\":9007199254740994", output.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, output.GetProperty("testSendId").ValueKind);
        var snapshot = host.Services.GetRequiredService<RouteDraftStore>().ReadGraphCompilation(request.ExpectedVersion, request.Graph, Token);
        var compiled = RoutePlanCompiler.Compile(snapshot, Token);
        var unit = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal));
        var actualTarget = transport.Build(new(unit.Id, RuntimeConnectionReferences.Protocol(unit).Configuration));
        var basis = result.GetProperty("result");
        var collected = basis.GetProperty("collectedAt").GetDateTimeOffset();
        var message = new EdgeMessage(basis.GetProperty("sourceMessageId").GetGuid(), "source", "sensor", collected,
            basis.GetProperty("timeSource").Deserialize<TimeSource>(), collected,
            new Dictionary<string, FieldValue> { ["scaled"] = FieldValue.FromInt64(9007199254740994) });
        var actual = actualTarget.CreateMessage(message, output.GetProperty("deliveryId").GetGuid());
        Assert.Equal(Encoding.UTF8.GetBytes(output.GetProperty("body").GetString()!), actual.Payload.ToArray());
        Assert.Equal(actual.Payload.Length, output.GetProperty("bodyBytes").GetInt32());
        Assert.Equal(actual.Address, output.GetProperty("address").GetString());
        if (decorated)
        {
            Assert.StartsWith("wire-format:output.wiremodule:", output.GetProperty("body").GetString(), StringComparison.Ordinal);
            Assert.Equal(actual.Headers!["X-Wire"], output.GetProperty("headers").GetProperty("X-Wire").GetString());
        }
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, transport.Sessions);
        Assert.Equal(0, transport.Adapters);
        Assert.False(listener.Pending());
    }
    [Theory]
    [InlineData("unsupported", "output.previewUnsupported")]
    [InlineData("address", "output.payload")]
    [InlineData("utf8", "output.payload")]
    [InlineData("exception", "output.payload")]
    [InlineData("oversize", "preview.size")]
    public async Task UnsupportedInvalidAndOversizedContentFailOnlyTheirPathAndNeverExposePluginExceptions(string invalid, string code)
    {
        using var data = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var credentials = new ForbiddenCredentials();
        await using var host = CreateHost(data.Path, credentials, supported: invalid != "unsupported");
        var request = Seed(host.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(request.ExpectedVersion, "http", "HTTP", ConnectionKind.Http, Http: new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/")), "fixture", null);
        request = request with
        {
            ExpectedVersion = connection.Version,
            Graph = request.Graph with
            {
                Nodes = [.. request.Graph.Nodes, new("good", "output.http", 1, "Good", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
                Edges = [.. request.Graph.Edges, new("map", "good")],
            },
        };
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        transport.ContentTransform = content => invalid switch
        {
            "address" => content with { Address = "other/fixture-secret" },
            "utf8" => content with { Payload = new byte[] { 0xff } },
            "exception" => throw new InvalidOperationException("fixture-secret"),
            "oversize" => content with { Payload = new byte[StandardEnvelope.MaximumPayloadBytes + 1] },
            _ => throw new InvalidOperationException("Unsupported formatter must not run."),
        };
        TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("editor", Token);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview", request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("fixture-secret", raw, StringComparison.Ordinal);
        var result = Json(raw).GetProperty("result");
        Assert.Equal(2, result.GetProperty("totalPaths").GetInt32());
        Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
        var failed = Assert.Single(result.GetProperty("paths").EnumerateArray(), path => !path.GetProperty("succeeded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("output").ValueKind);
        Assert.Equal(code, Assert.Single(failed.GetProperty("issues").EnumerateArray()).GetProperty("code").GetString());
        var good = Assert.Single(result.GetProperty("paths").EnumerateArray(), path => path.GetProperty("succeeded").GetBoolean());
        Assert.Equal("output.http", good.GetProperty("output").GetProperty("kind").GetString());
        Assert.Contains("9007199254740994", good.GetProperty("output").GetProperty("body").GetString(), StringComparison.Ordinal);
        AssertNoPreviewEffects(host, credentials, transport, request.ExpectedVersion);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task LargeExtensionContentKeepsFullByteCountButUsesTheExisting64KiBDisplayBudgetWithoutTestSend()
    {
        using var data = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var credentials = new ForbiddenCredentials();
        await using var host = CreateHost(data.Path, credentials);
        var request = Seed(host.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        var body = string.Concat(Enumerable.Repeat("界😀", 30000));
        transport.ContentTransform = content => content with { Payload = Encoding.UTF8.GetBytes(body) };
        TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("editor", Token);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview", request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.True(bytes.Length <= 64 * 1024);
        using var json = JsonDocument.Parse(bytes);
        var result = json.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("truncated").GetBoolean());
        var path = Assert.Single(result.GetProperty("paths").EnumerateArray());
        Assert.True(path.GetProperty("succeeded").GetBoolean());
        var output = path.GetProperty("output");
        Assert.True(output.GetProperty("truncated").GetBoolean());
        Assert.Equal(210000, output.GetProperty("bodyBytes").GetInt32());
        var shown = output.GetProperty("body").GetString()!;
        Assert.NotEmpty(shown);
        Assert.StartsWith(shown, body, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(shown[^1]));
        Assert.Equal(JsonValueKind.Null, output.GetProperty("testSendId").ValueKind);
        AssertNoPreviewEffects(host, credentials, transport, request.ExpectedVersion);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("Viewer", true, HttpStatusCode.Forbidden)]
    [InlineData("Configurator", false, HttpStatusCode.Forbidden)]
    [InlineData("Administrator", false, HttpStatusCode.Forbidden)]
    [InlineData("Anonymous", false, HttpStatusCode.Unauthorized)]
    public async Task UnauthorizedRequestsNeverInvokeExtensionFormatting(string role, bool csrf, HttpStatusCode expected)
    {
        using var data = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var credentials = new ForbiddenCredentials();
        await using var host = CreateHost(data.Path, credentials);
        var request = Seed(host.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        var calls = 0;
        transport.ContentTransform = content => { Interlocked.Increment(ref calls); return content; };
        if (role != "Anonymous")
        { TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role)); }
        using var client = role == "Anonymous" ? host.CreateClient() : await host.CreateLoggedInClientAsync("operator", Token);
        if (!csrf)
        { client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview", request, Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, calls);
        AssertNoPreviewEffects(host, credentials, transport, request.ExpectedVersion);
        Assert.False(listener.Pending());
    }

    private static void AssertNoPreviewEffects(FixedDataDirectoryApplicationFactory host, ForbiddenCredentials credentials, TestWireOutputTransport transport, string version)
    {
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, transport.Builds);
        Assert.Equal(0, transport.Adapters);
        Assert.Equal(0, transport.Sessions);
        Assert.Equal(0, transport.QueueBindings);
        Assert.Equal(0, host.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Equal(version, host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
    }

    private static FixedDataDirectoryApplicationFactory CreateHost(string directory, ForbiddenCredentials credentials, bool supported = true) =>
        new(directory, configureServices: services => services.AddSingleton(TestWireOutputModule.Definition)
            .AddSingleton<IOutputModuleFactory, TestWireOutputModule>()
            .AddSingleton<IModuleCredentialResolver>(credentials)
            .AddSingleton<TestWireOutputTransport>()
            .AddSingleton<IOutputAdapterFactory>(provider => supported
                ? provider.GetRequiredService<TestWireOutputTransport>() : new WithoutFormatter()));

    private static RoutePreviewRequest Seed(IServiceProvider services, int port)
    {
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var target = connections.Write(null, new(connections.Read().Version, "wire", "Wire", ConnectionKind.Module,
            Module: new("wire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = port }))), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(target.Version, "sensor", "Sensor", DeviceKind.Simulator), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "source", "Source", Simulator: new(PeriodMs: 3600000, SampleJson: "{\"value\":9007199254740993}")), "fixture", null);
        var graph = new RouteGraph(1,
        [
            new("in", "input.simulator", 1, "Source", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
            new("map", "transform.map", 1, "Map", Json("""{"rules":[{"sourceField":"value","target":"scaled","type":"integer","offset":1}]}""")),
            new("out", "output.wiremodule", 1, "Output", JsonSerializer.SerializeToElement(new
            {
                connectionId = target.Connection.Id,
                parameters = new { address = "events/{deviceKey}" },
                connections = new { connection = target.Connection.Id },
            })),
        ], [new("in", "map"), new("map", "out")], new([]));
        return new(input.Version, graph, input.Input.Id, "{\"value\":9007199254740993}");
    }

    private sealed class WithoutFormatter : IOutputAdapterFactory
    {
        public string Kind => "output.wire";
        public IOutputTarget Build(RuntimeUnitPlan plan) => throw new InvalidOperationException("Preview must not build a target.");
    }

    private sealed class ForbiddenCredentials : IModuleCredentialResolver
    {
        public int Calls { get; private set; }
        public ModuleCredentialIdentity? DescribeModule(long connectionId)
        { Calls++; throw new InvalidOperationException("Preview must not read credential identity."); }
        public ModuleCredential? ResolveModule(ModuleConnectionSnapshot connection)
        { Calls++; throw new InvalidOperationException("Preview must not resolve secrets."); }
    }
}

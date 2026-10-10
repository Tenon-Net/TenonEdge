using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteHttpPreviewTests
{
    private const string Endpoint = "/api/edge-admin/v1/routes/preview/http";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task ExplicitHttpReadUsesRealWireAndExactSampleWithoutContactingOutputOrWritingDraft(string role, HttpStatusCode expected)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var output = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        output.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, Port(source), Port(output));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var before = DateTimeOffset.UtcNow;
        var peer = expected == HttpStatusCode.OK ? Reply(source, """{"value":9223372036854775807}""") : Task.CompletedTask;
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True((await response.Content.ReadAsByteArrayAsync(Token)).Length <= 65536);
            var json = await response.ReadJsonAsync(Token);
            Assert.True(json.GetProperty("previewed").GetBoolean());
            var result = json.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
            Assert.Equal("9223372036854775807", result.GetProperty("source").GetProperty("fields")[0].GetProperty("text").GetString());
            Assert.Equal("""{"result":9223372036854775807}""", result.GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString());
            Assert.InRange(result.GetProperty("collectedAt").GetDateTimeOffset(), before, DateTimeOffset.UtcNow);
        }
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(source.Pending());
        Assert.False(output.Pending());
        Assert.Equal(request.ExpectedVersion, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Empty(factory.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
    }

    [Theory]
    [InlineData("redacted", "preview.http.payload.redacted")]
    [InlineData("large", "preview.http.payload.tooLarge")]
    [InlineData("invalid", "preview.http.payload.invalidJson")]
    public async Task UnsafeOrIncompleteSamplesAreNotUsedForRouteCalculation(string kind, string code)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, Port(source), 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var body = kind switch
        {
            "redacted" => """{"password":"fixture-secret","value":42}""",
            "large" => JsonSerializer.Serialize(new { value = 42, padding = new string('x', 1024 * 1024) }),
            _ => "not-json"
        };
        var peer = Reply(source, body, allowDisconnect: kind == "large");
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("succeededPaths", text, StringComparison.Ordinal);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Theory]
    [InlineData(40000)]
    [InlineData(700000)]
    public async Task FullHttpBodyBeyondDisplayBudgetReachesRawPointerMapping(int padding)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var output = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        output.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, Port(source), Port(output));
        request = request with
        {
            Graph = request.Graph with
            {
                Nodes = [.. request.Graph.Nodes,
                    new("map", "transform.map", 1, "Map", JsonSerializer.Deserialize<JsonElement>(
                        """{"rules":[{"target":"value","sourcePath":"/tail","type":"integer","required":true}]}"""))],
                Edges = [new("in", "map"), new("map", "out")]
            }
        };
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var peer = Reply(source, "{\"value\":1,\"padding\":\"" + new string('x', padding) + "\",\"tail\":9223372036854775807}");
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.True(bytes.Length <= 65536);
        using var json = JsonDocument.Parse(bytes);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
        Assert.Equal("""{"result":9223372036854775807}""",
            result.GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString());
        Assert.False(output.Pending());
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task AnonymousCsrfStaleAndForeignSourceAreRejectedBeforeNetwork()
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, Port(source), 1);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var stale = await client.PostJsonAsync(Endpoint, request with { ExpectedVersion = "stale" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(Endpoint, request with { SourceId = request.SourceId + 1 }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.False(source.Pending());
    }

    [Fact]
    public async Task SharedAdmissionRejectsBeforeAcquisitionAndReleasesAfterValidationFailure()
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, Port(source), 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using (var first = budget.TryAcquire(key))
        using (var second = budget.TryAcquire(key))
        {
            using var rejected = await client.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }
        for (var index = 0; index < 3; index++)
        {
            using var invalid = await client.PostJsonAsync(Endpoint, request with { SourceId = -1 }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.False(source.Pending());
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    private static RouteHttpPreviewRequest Seed(IServiceProvider services, int sourcePort, int outputPort)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [sourcePort, outputPort]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var source = connections.Write(null, new(connections.Read().Version, "source-http", "Source", ConnectionKind.Http,
            Http: new($"http://127.0.0.1:{sourcePort}/")), "fixture", null);
        var output = connections.Write(null, new(source.Version, "output-http", "Output", ConnectionKind.Http,
            Http: new($"http://127.0.0.1:{outputPort}/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(output.Version, "device", "Device", DeviceKind.Http, ConnectionId: source.Connection.Id), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Http: new("values", [new("value", "/value", FieldType.Integer)])), "fixture", null);
        var graph = new RouteGraph(1,
            [new("in", "input.http", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
            new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = output.Connection.Id,
                payload = new { mode = "custom", fields = new[] { new { target = "result", sourceField = "value" } } } }))],
            [new("in", "out")], new([]));
        return new(input.Version, graph, input.Input.Id);
    }
    private static async Task Reply(TcpListener listener, string body, bool allowDisconnect = false)
    {
        using var socket = await listener.AcceptTcpClientAsync(Token);
        var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("GET /values HTTP/1.1", await reader.ReadLineAsync(Token));
        while (await reader.ReadLineAsync(Token) is { Length: > 0 })
        { }
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\n\r\n"), Token);
        try
        { await stream.WriteAsync(bytes, Token); }
        catch (IOException) when (allowDisconnect) { } // declared oversize body may be rejected before it is read
    }
}

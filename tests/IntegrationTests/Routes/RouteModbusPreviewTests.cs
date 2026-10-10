using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Modbus;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteModbusPreviewTests
{
    private const string Endpoint = "/api/edge-admin/v1/routes/preview/modbus";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK, false)]
    [InlineData("Configurator", HttpStatusCode.OK, true)]
    [InlineData("Viewer", HttpStatusCode.Forbidden, false)]
    public async Task RealReadKeepsExactIntegersAndNeverConnectsOutputOrChangesDrafts(string role, HttpStatusCode expected, bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        using var output = new TcpListener(IPAddress.Loopback, 0);
        output.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, slave.Endpoint.Port, ((IPEndPoint)output.LocalEndpoint).Port, rtu);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True((await response.Content.ReadAsByteArrayAsync(Token)).Length <= 65536);
            var json = await response.ReadJsonAsync(Token);
            Assert.True(json.GetProperty("previewed").GetBoolean());
            var result = json.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
            var exact = 0x1000100110021003UL.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(exact, result.GetProperty("source").GetProperty("fields")[0].GetProperty("text").GetString());
            Assert.Equal("{\"result\":" + exact + "}", result.GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString());
            Assert.Equal(1, slave.ConnectionCount);
            Assert.Equal(1, slave.RequestCount);
        }
        else
        { Assert.Equal(0, slave.ConnectionCount); }
        Assert.False(output.Pending());
        Assert.Equal(request.ExpectedVersion, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Empty(factory.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialFailuresKeepBadQualityButAllFailedReadsCannotPreview(bool allFail)
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => allFail || request.Address == 10 ? new SlaveReply { ExceptionCode = 2 } : SlaveReply.Normal);
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, slave.Endpoint.Port, 1, false, true);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(allFail ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
        var json = await response.ReadJsonAsync(Token);
        if (allFail)
        { Assert.Equal("preview.modbus.readFailed", json.GetProperty("code").GetString()); }
        else
        {
            var result = json.GetProperty("result");
            var bad = result.GetProperty("source").GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("key").GetString() == "bad");
            Assert.Equal("bad", bad.GetProperty("quality").GetString());
            Assert.Equal("deviceException", bad.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, bad.GetProperty("text").ValueKind);
            Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
        }
    }

    [Fact]
    public async Task AnonymousCsrfStaleVersionAndOutsideSourceAreRejectedBeforeNetwork()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Seed(factory.Services, slave.Endpoint.Port, 1);
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
        Assert.Equal(0, slave.ConnectionCount);
    }

    private static RouteModbusPreviewRequest Seed(IServiceProvider services, int port, int outputPort, bool rtu = false, bool partial = false)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port, outputPort]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var source = connections.Write(null, new(connections.Read().Version, "modbus", "Modbus", ConnectionKind.Modbus,
            Modbus: new("127.0.0.1", port, rtu ? ModbusFrameFormat.RtuOverTcp : ModbusFrameFormat.Tcp, RtuSilenceMs: 100)), "fixture", null);
        var output = connections.Write(null, new(source.Version, "output", "Output", ConnectionKind.Http,
            Http: new($"http://127.0.0.1:{outputPort}/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(output.Version, "device", "Device", DeviceKind.Modbus, ConnectionId: source.Connection.Id, UnitId: rtu ? 1 : 255), "fixture", null);
        List<ModbusPointSettings> points = [new() { Key = "value", FunctionCode = 3, Address = 0, Type = "uint64", ByteOrder = "ABCD" }];
        if (partial)
        { points.Add(new() { Key = "bad", FunctionCode = 3, Address = 10, Type = "uint16" }); }
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Modbus: new(5000, points.ToArray())), "fixture", null);
        var graph = new RouteGraph(1,
            [new("in", "input.modbus", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
            new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = output.Connection.Id,
                payload = new { mode = "custom", fields = new[] { new { target = "result", sourceField = "value" } } } }))],
            [new("in", "out")], new([]));
        return new(input.Version, graph, input.Input.Id);
    }
}

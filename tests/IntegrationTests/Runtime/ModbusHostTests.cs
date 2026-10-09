using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ModbusHostTests
{
    private static readonly string[] Inputs = ["meter/fast"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeededHostReadsRealModbusAndDurablyDeliversToHttp(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        using var sink = new TcpListener(IPAddress.Loopback, 0);
        sink.Start();
        var port = ((IPEndPoint)sink.LocalEndpoint).Port;
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var api = bootstrap.CreateClient();
            using var setup = await api.CompleteSetupAsync(Token);
        }
        var seed = Path.Combine(seeds.Path, "modbus-http.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new object[]
            {
                new { kind = "input.modbus", key = "meter/fast", configuration = new
                {
                    connectionId = 1, host = "127.0.0.1", port = slave.Endpoint.Port, unitId = rtu ? 1 : 255,
                    frameFormat = rtu ? "rtu-over-tcp" : "tcp", periodMs = 5000,
                    points = new[] { new { key = "energy", functionCode = 3, address = 0, type = "uint64", byteOrder = "ABCD" } }
                } },
                new { kind = "output.http", key = "sink", configuration = new
                { connectionId = 2, baseAddress = $"http://127.0.0.1:{port}/", path = "events", inputs = Inputs } }
            }
        }), Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var received = ReceiveAsync(sink, timeout.Token);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            [RuntimeSettings.SeedSnapshotKey] = seed,
            ["TenonEdge:Outbound:LocalPorts:0"] = slave.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = port.ToString(CultureInfo.InvariantCulture)
        });
        using var started = host.CreateClient();
        using var body = JsonDocument.Parse(await received.WaitAsync(TimeSpan.FromSeconds(20), Token));
        Assert.Equal(0x1000100110021003UL, body.RootElement.GetProperty("values").GetProperty("energy").GetUInt64());
        Assert.StartsWith("gateway", body.RootElement.GetProperty("timeSource").GetString(), StringComparison.Ordinal);
        Assert.Equal("meter", body.RootElement.GetProperty("deviceKey").GetString());
        Assert.NotEqual(Guid.Empty, body.RootElement.GetProperty("sourceMessageId").GetGuid());
        Assert.EndsWith("Z", body.RootElement.GetProperty("collectedAt").GetString(), StringComparison.Ordinal);
        await OutputPipelineTests.UntilAsync(() => host.Services.GetRequiredService<DeliveryCache>().GetStatus().DeletedRecords >= 1);
        using var admin = await host.CreateLoggedInClientAsync("admin", Token);
        var status = await admin.GetJsonAsync("/api/edge-admin/v1/runtime/status", Token);
        var input = Assert.Single(status.GetProperty("runtime").GetProperty("units").EnumerateArray(),
            unit => unit.TryGetProperty("reads", out var reads) && reads.ValueKind == JsonValueKind.Object);
        Assert.True(input.GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 1);
        Assert.Equal(1, slave.ConnectionCount);
        Assert.All(slave.Requests, request => Assert.Equal((byte)(rtu ? 1 : 255), request.Unit));
    }

    private static async Task<byte[]> ReceiveAsync(TcpListener listener, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        var stream = socket.GetStream();
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            { break; }
        }
        var headers = Encoding.ASCII.GetString(bytes.ToArray());
        Assert.StartsWith("POST /events HTTP/1.1", headers, StringComparison.Ordinal);
        var length = int.Parse(headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
        Assert.InRange(length, 1, 256 * 1024);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        return body;
    }
}

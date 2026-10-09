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
    private static readonly string[] FanInInputs = ["meter/fast", "second/fast"];
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

    [Fact]
    public async Task OneModbusSampleFansOutWithIndependentMappingFailureAndUnresponsiveTarget()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        using var originalSink = new TcpListener(IPAddress.Loopback, 0);
        using var mappedSink = new TcpListener(IPAddress.Loopback, 0);
        using var unresponsiveSink = new TcpListener(IPAddress.Loopback, 0);
        originalSink.Start();
        mappedSink.Start();
        unresponsiveSink.Start();
        var originalPort = ((IPEndPoint)originalSink.LocalEndpoint).Port;
        var mappedPort = ((IPEndPoint)mappedSink.LocalEndpoint).Port;
        var unresponsivePort = ((IPEndPoint)unresponsiveSink.LocalEndpoint).Port;
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var api = bootstrap.CreateClient();
            using var setup = await api.CompleteSetupAsync(Token);
        }
        var seed = Path.Combine(seeds.Path, "modbus-fanout.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new object[]
            {
                new { kind = "input.modbus", key = "meter/fast", configuration = new
                {
                    connectionId = 1, host = "127.0.0.1", port = slave.Endpoint.Port, unitId = 1, periodMs = 60000,
                    points = new[] { new { key = "energy", functionCode = 3, address = 0, type = "uint64", byteOrder = "ABCD" } }
                } },
                new { kind = "output.http", key = "original", configuration = new
                { connectionId = 2, baseAddress = $"http://127.0.0.1:{originalPort}/", path = "events", inputs = Inputs } },
                new { kind = "output.http", key = "mapped", configuration = new
                {
                    connectionId = 3, baseAddress = $"http://127.0.0.1:{mappedPort}/", path = "events", inputs = Inputs,
                    mapping = new[] { new { target = "asText", type = "string", sourceField = "energy", required = true } }
                } },
                new { kind = "output.http", key = "a-unresponsive", configuration = new
                { connectionId = 4, baseAddress = $"http://127.0.0.1:{unresponsivePort}/", path = "events", timeoutMs = 60000, inputs = Inputs } },
                new { kind = "output.http", key = "rejected", configuration = new
                {
                    connectionId = 5, baseAddress = $"http://127.0.0.1:{originalPort}/", path = "events", inputs = Inputs,
                    mapping = new[] { new { target = "missing", type = "integer", sourceField = "absent", required = true } }
                } }
            }
        }), Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var originalRead = ReceiveAsync(originalSink, timeout.Token);
        var mappedRead = ReceiveAsync(mappedSink, timeout.Token);
        var blockedArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRead = ReceiveAsync(unresponsiveSink, timeout.Token, blockedArrived, releaseBlocked.Task, respond: false);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            [RuntimeSettings.SeedSnapshotKey] = seed,
            ["TenonEdge:Outbound:LocalPorts:0"] = slave.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = originalPort.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:2"] = mappedPort.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:3"] = unresponsivePort.ToString(CultureInfo.InvariantCulture)
        });
        using var started = host.CreateClient();
        await blockedArrived.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
        using var original = JsonDocument.Parse(await originalRead.WaitAsync(TimeSpan.FromSeconds(20), Token));
        using var mapped = JsonDocument.Parse(await mappedRead.WaitAsync(TimeSpan.FromSeconds(20), Token));
        // 故障目标仍在等待响应、尚未超时，两个正常目标已真实收到数据。
        Assert.False(blockedRead.IsCompleted);
        releaseBlocked.SetResult();
        var blockedWire = await blockedRead.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        var state = new RuntimeStateReader(data.Path);
        await OutputPipelineTests.UntilAsync(() => cache.GetStatus().DeletedRecords == 2 &&
            cache.GetPersistentCounters().GetValueOrDefault("path/meter/fast/5/dataRejected") == 1 && state.Events("output.retry").Count > 0);
        Assert.Single(slave.Requests);
        Assert.Equal(1, slave.ConnectionCount);
        Assert.Equal(0x1000100110021003UL, original.RootElement.GetProperty("values").GetProperty("energy").GetUInt64());
        Assert.False(original.RootElement.GetProperty("values").TryGetProperty("asText", out _));
        Assert.Equal(0x1000100110021003UL.ToString(CultureInfo.InvariantCulture), mapped.RootElement.GetProperty("values").GetProperty("asText").GetString());
        Assert.False(mapped.RootElement.GetProperty("values").TryGetProperty("energy", out _));
        Assert.Equal(original.RootElement.GetProperty("sourceMessageId").GetGuid(), mapped.RootElement.GetProperty("sourceMessageId").GetGuid());
        Assert.NotEqual(original.RootElement.GetProperty("messageId").GetGuid(), mapped.RootElement.GetProperty("messageId").GetGuid());
        var held = Assert.Single(cache.ReadPending(4, 0, 10));
        Assert.Equal(original.RootElement.GetProperty("sourceMessageId").GetGuid(), held.SourceMessageId);
        Assert.NotEqual(original.RootElement.GetProperty("messageId").GetGuid(), held.DeliveryId);
        Assert.NotEqual(mapped.RootElement.GetProperty("messageId").GetGuid(), held.DeliveryId);
        Assert.Equal(held.Payload.ToArray(), blockedWire);
        Assert.Equal(0, held.FailureCount); // 网络失败不累积为毒消息。
        Assert.Empty(cache.ReadPending(5, 0, 10));
        Assert.Equal(3, cache.GetStatus().CommittedRecords);
        var runtime = host.Services.GetRequiredService<TenonEdge.Runtime.Lifecycle.RuntimeHost>();
        await OutputPipelineTests.UntilAsync(() => runtime.InFlight.Status.ReservedBytes == 0);
        Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 1, runtime.InFlight.Status.CapacityBytes);
    }

    [Fact]
    public async Task TwoModbusInputsFanInWithoutJoiningOrReplacingDeviceIdentity()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
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
        object Input(string key, byte unit, int address) => new
        {
            kind = "input.modbus",
            key,
            configuration = new
            {
                connectionId = 1,
                host = "127.0.0.1",
                port = slave.Endpoint.Port,
                unitId = unit,
                periodMs = 60000,
                points = new[] { new { key = "value", functionCode = 3, address, type = "uint16" } }
            }
        };
        var seed = Path.Combine(seeds.Path, "modbus-fanin.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new[]
            {
                Input("meter/fast", 1, 0), Input("second/fast", 2, 10),
                new { kind = "output.http", key = "sink", configuration = new
                { connectionId = 2, baseAddress = $"http://127.0.0.1:{port}/", path = "events", inputs = FanInInputs } }
            }
        }), Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var firstRead = ReceiveAsync(sink, timeout.Token);
        var secondRead = ReceiveAsync(sink, timeout.Token);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            [RuntimeSettings.SeedSnapshotKey] = seed,
            ["TenonEdge:Outbound:LocalPorts:0"] = slave.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = port.ToString(CultureInfo.InvariantCulture)
        });
        using var started = host.CreateClient();
        using var first = JsonDocument.Parse(await firstRead.WaitAsync(TimeSpan.FromSeconds(20), Token));
        using var second = JsonDocument.Parse(await secondRead.WaitAsync(TimeSpan.FromSeconds(20), Token));
        var messages = new[] { first.RootElement, second.RootElement }.ToDictionary(message => message.GetProperty("deviceKey").GetString()!);
        Assert.Equal(4096, messages["meter"].GetProperty("values").GetProperty("value").GetInt32());
        Assert.Equal(4106, messages["second"].GetProperty("values").GetProperty("value").GetInt32());
        Assert.All(messages.Values, message => Assert.Single(message.GetProperty("values").EnumerateObject()));
        Assert.NotEqual(first.RootElement.GetProperty("sourceMessageId").GetGuid(), second.RootElement.GetProperty("sourceMessageId").GetGuid());
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        await OutputPipelineTests.UntilAsync(() => cache.GetStatus().DeletedRecords == 2);
        Assert.Equal(2, cache.GetStatus().CommittedRecords);
        Assert.Equal(2, slave.RequestCount);
        Assert.Equal(1, slave.ConnectionCount);
    }

    private static async Task<byte[]> ReceiveAsync(TcpListener listener, CancellationToken token,
        TaskCompletionSource? arrived = null, Task? waitForReply = null, bool respond = true)
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
        arrived?.TrySetResult();
        if (waitForReply is not null)
        { await waitForReply.WaitAsync(token); }
        if (!respond)
        { return body; }
        await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        return body;
    }
}

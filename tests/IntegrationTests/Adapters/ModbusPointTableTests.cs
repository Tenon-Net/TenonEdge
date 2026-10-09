using System.Net;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusPointTableTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParsedPlanThroughRealWireProducesKnownCurrentValuesAndQuality(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.FunctionCode == 4 });
        using var json = JsonDocument.Parse($$"""
            {"connectionId":1,"host":"127.0.0.1","port":{{slave.Endpoint.Port}},
             "frameFormat":"{{(rtu ? "rtu-over-tcp" : "tcp")}}","unitId":1,"readTimeoutMs":500,"rtuSilenceMs":100,
             "points":[
               {"key":"coil","functionCode":1,"address":2,"type":"bool"},
               {"key":"discrete","functionCode":2,"address":3,"type":"bool"},
               {"key":"large","functionCode":3,"address":0,"type":"uint64","byteOrder":"ABCD"},
               {"key":"flag","functionCode":3,"address":0,"type":"bit","bit":12},
               {"key":"failed","functionCode":4,"address":10,"type":"uint16"},
               {"key":"skipped","functionCode":4,"address":20,"type":"uint16"}
             ]}
            """);
        var settings = ModbusInputSettings.Parse(json.RootElement);
        var table = new ModbusPointTable(settings.Points, settings.MaxRegisters, settings.MaxBits);
        var shared = new SharedConnections();
        await using var lease = shared.Acquire(1, "modbus", settings.Connection.Identity,
            () => new ModbusConnectionSession(settings.Connection, [1, 2], new OutboundConnector(new LoopbackPolicy()), TimeProvider.System));
        var cycle = await ModbusCycleReader.ReadAsync(lease, settings.UnitId!.Value, table.Blocks, TimeSpan.FromSeconds(5), TimeProvider.System, Token);
        var now = DateTimeOffset.UtcNow;
        var message = table.Decode(cycle, "fast", "device", now, TimeSource.GatewayUnsynced)!;
        Assert.Equal(FieldValue.FromBoolean(true), message.Values["coil"]);
        Assert.Equal(FieldValue.FromBoolean(false), message.Values["discrete"]);
        Assert.Equal(FieldValue.FromBoolean(true), message.Values["flag"]);
        // 独立手写已知线序 1000 1001 1002 1003，不调用被测解码器生成期望。
        Assert.Equal(FieldValue.FromUInt64(0x1000100110021003UL), message.Values["large"]);
        Assert.Equal(QualityCode.ReadTimeout, message.Quality["failed"]);
        Assert.Equal(QualityCode.ReadTimeout, message.Quality["skipped"]);
        Assert.Equal(4, message.Values.Count);
        Assert.Equal(2, message.Quality.Count);
        Assert.Equal(now, message.CollectedAt);
        Assert.Equal(TimeSource.GatewayUnsynced, message.TimeSource);
        Assert.Equal(4, slave.RequestCount);
        Assert.DoesNotContain(slave.Requests, request => request.Address == 20);
        Assert.Equal(1, slave.ConnectionCount);
    }

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}

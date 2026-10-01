using System.Net.Sockets;
using static TenonEdge.IntegrationTests.Probes.Modbus.ModbusProbe;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>
/// T0-12 Modbus 候选库比较（V-05 比较部分，验证代码，不是正式适配器）：两种帧格式下的基线读取，以及显式读取超时与库内重试。
/// 断言固定的是 NModbus 3.0.83 与 FluentModbus 5.3.2 本身的表现；适配层要补的行为见 docs/implementation-status.md §2.12。
/// <list type="bullet">
/// <item>基线读取只证明能连通、能解析，不单独作为通过依据。</item>
/// <item>两库的读取超时默认都是无限，须显式设置；NModbus 默认还会重试 3 次、每次间隔 250 毫秒，一次读取的耗时因此是超时的 4 倍以上。</item>
/// <item>FluentModbus 的异步读取超时后会关闭连接，下一次读取须重新连接；超时抛出的异常类型不确定，不能据异常类型判断是否超时。</item>
/// </list>
/// </summary>
[Trait("Category", "ModbusProbe")]
public sealed class ModbusReadAndTimeoutComparisonTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AllCombinations), MemberType = typeof(ModbusProbe))]
    public async Task BothFramingsReadKnownRegisterValues(ModbusLibrary library, ModbusFraming framing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(framing);
        using var client = ModbusProbeClient.Connect(library, framing, slave.Endpoint, ModbusProbeOptions.Default);

        Assert.Equal(ModbusTestSlave.RegisterValues(10, 4), client.Read(1, 10, 4));
        Assert.Equal(ModbusTestSlave.RegisterValues(200, 2), await client.ReadAsync(1, 200, 2, cancellationToken));
        Assert.All(slave.Requests, request => Assert.Equal((1, 3), (request.Unit, request.FunctionCode)));
        Assert.Equal(2, slave.RequestCount);
    }

    [Theory]
    [MemberData(nameof(AllCombinations), MemberType = typeof(ModbusProbe))]
    public async Task AnExplicitReadTimeoutEndsTheReadWithoutRetries(ModbusLibrary library, ModbusFraming framing)
    {
        await using var slave = ModbusTestSlave.Start(framing, _ => new SlaveReply { Silent = true });
        using var client = ModbusProbeClient.Connect(library, framing, slave.Endpoint, ModbusProbeOptions.Default);

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"{library} {framing}：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");

        // 同步读取超时表现为 IOException（内部为套接字超时），只发出一个请求。
        var timeout = Assert.IsType<IOException>(exception);
        Assert.Equal(SocketError.TimedOut, Assert.IsType<SocketException>(timeout.InnerException).SocketErrorCode);
        Assert.InRange(elapsed, ReadTimeout - TimeSpan.FromMilliseconds(50), ReadTimeout + Slack);
        Assert.Equal(1, slave.RequestCount);
    }

    [Fact]
    public async Task NModbusDefaultsRetryThreeTimesSoOneReadTakesFourTimeouts()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        using var client = NModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default with { Retries = null });

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"NModbus 默认重试：{elapsed.TotalMilliseconds:F0} ms，{slave.RequestCount} 个请求，{Describe(exception)}");

        // 4 次发送（每次新的事务号）、3 次 250 毫秒的等待：4 × 300 + 3 × 250 = 1950 毫秒。
        Assert.Equal(3, client.Transport.Retries);
        Assert.IsType<IOException>(exception);
        Assert.Equal(4, slave.RequestCount);
        Assert.Equal(4, slave.Requests.Select(request => request.TransactionId).Distinct().Count());
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(1900), TimeSpan.FromMilliseconds(1950) + Slack);
    }

    [Fact]
    public async Task NModbusWithOneRetrySendsTheRequestTwice()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp, _ => new SlaveReply { Silent = true });
        using var client = NModbusProbeClient.Connect(ModbusFraming.RtuOverTcp, slave.Endpoint, ModbusProbeOptions.Default with { Retries = 1 });

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"NModbus 重试 1 次：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");

        Assert.IsType<IOException>(exception);
        Assert.Equal(2, slave.RequestCount);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(850) + Slack);
    }

    [Theory]
    [InlineData(ModbusFraming.Tcp)]
    [InlineData(ModbusFraming.RtuOverTcp)]
    public async Task FluentModbusAsyncTimeoutClosesTheConnection(ModbusFraming framing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(framing, request => request.Sequence == 1 ? new SlaveReply { Silent = true } : SlaveReply.Normal);
        using var client = FluentModbusProbeClient.Connect(framing, slave.Endpoint, ModbusProbeOptions.Default);

        var (timeout, elapsed) = await MeasureAsync(() => client.ReadAsync(1, 0, 2, cancellationToken));
        var next = await Record.ExceptionAsync(() => client.ReadAsync(1, 0, 2, cancellationToken));
        output.WriteLine($"FluentModbus {framing} 异步：{elapsed.TotalMilliseconds:F0} ms，{Describe(timeout)}；下一次读取：{Describe(next)}");

        // 超时时库关闭网络流，挂起的读取随之以三种方式之一结束：读取抛出异常时报 TimeoutException；读到 0 字节时，
        // TCP 客户端报“连接意外关闭”（InvalidOperationException），RTU over TCP 客户端再读一次报 ObjectDisposedException
        // （它是 InvalidOperationException 的子类）。结果取决于时序（已记录的库行为）。之后连接已关闭，下一次读取不再发出请求。
        Assert.True(timeout is TimeoutException or InvalidOperationException, $"超时以意外的异常结束：{Describe(timeout)}");
        Assert.InRange(elapsed, ReadTimeout - TimeSpan.FromMilliseconds(50), ReadTimeout + Slack);
        Assert.IsType<ObjectDisposedException>(next);
        Assert.Equal(1, slave.RequestCount);
    }
}

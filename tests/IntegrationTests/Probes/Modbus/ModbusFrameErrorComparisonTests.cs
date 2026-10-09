using static TenonEdge.IntegrationTests.Probes.Modbus.ModbusProbe;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>
/// T0-12 Modbus 候选库比较（V-05 比较部分，验证代码，不是正式适配器）：RTU 应答校验失败与对端关闭连接。
/// <list type="bullet">
/// <item>CRC 错误、从站地址不符：NModbus 立即报帧错误；FluentModbus 把不合格的帧当作“还没收完”，一直等到读取超时，诊断上无法与不应答区分。</item>
/// <item>对端关闭连接：NModbus 与 FluentModbus 的 TCP 客户端立即报错；FluentModbus 的 RTU over TCP 客户端不检查读到 0 字节，
/// 在已关闭的连接上空转，读取超时不起作用，只有释放连接或取消才能结束（已记录的缺陷）。它的异步读取在每次读取都同步返回 0 时
/// 从不让出线程：调用方在调用 ReadAsync 的那个线程上空转、调用不返回，只能由其他线程取消。这一点的证据是 T0-C 审查时
/// 用 dotnet-stack 抓到的线程栈（docs/implementation-status.md §4.9）；下面的用例为避免挂死改在线程池线程上调用，只证明空转与取消。</item>
/// </list>
/// </summary>
[Trait("Category", "ModbusProbe")]
public sealed class ModbusFrameErrorComparisonTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NModbusReportsAnRtuCrcErrorImmediately()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp, _ => new SlaveReply { CorruptCrc = true });
        using var client = ModbusProbeClient.Connect(ModbusLibrary.NModbus, ModbusFraming.RtuOverTcp, slave.Endpoint, ModbusProbeOptions.Default);

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"NModbus CRC 错误：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");
        output.WriteLine($"从站观测：连接 {slave.ConnectionCount}，收到请求 {slave.RequestCount}，写出应答 {slave.ResponseCount}。");

        Assert.Contains("Checksums failed to match", Assert.IsType<IOException>(exception).Message, StringComparison.Ordinal);
        Assert.True(elapsed < ReadTimeout, $"用了 {elapsed}，应在读取超时之前报错。");
    }

    [Fact]
    public async Task NModbusReportsAnRtuResponseFromAnotherUnitImmediately()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp, request => new SlaveReply { Unit = (byte)(request.Unit + 1) });
        using var client = ModbusProbeClient.Connect(ModbusLibrary.NModbus, ModbusFraming.RtuOverTcp, slave.Endpoint, ModbusProbeOptions.Default);

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"NModbus 从站地址不符：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");

        Assert.Contains("slave address does not match", Assert.IsType<IOException>(exception).Message, StringComparison.Ordinal);
        Assert.True(elapsed < ReadTimeout, $"用了 {elapsed}，应在读取超时之前报错。");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FluentModbusWaitsForTheReadTimeoutOnAnInvalidRtuFrame(bool corruptCrc, bool otherUnit)
    {
        await using var slave = ModbusTestSlave.Start(
            ModbusFraming.RtuOverTcp,
            request => new SlaveReply { CorruptCrc = corruptCrc, Unit = otherUnit ? (byte)(request.Unit + 1) : null });
        using var client = ModbusProbeClient.Connect(ModbusLibrary.FluentModbus, ModbusFraming.RtuOverTcp, slave.Endpoint, ModbusProbeOptions.Default);

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"FluentModbus（CRC 错误 {corruptCrc}，从站地址不符 {otherUnit}）：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");

        // 不合格的帧被当作“尚未收完”，读取在超时后才以超时结束。
        Assert.IsType<IOException>(exception);
        Assert.InRange(elapsed, ReadTimeout - TimeSpan.FromMilliseconds(50), ReadTimeout + Slack);
    }

    [Theory]
    [MemberData(nameof(ClosingCombinations))]
    public async Task AConnectionClosedByThePeerIsReportedImmediately(ModbusLibrary library, ModbusFraming framing, Type expectedType, string expectedMessage)
    {
        await using var slave = ModbusTestSlave.Start(framing, _ => new SlaveReply { CloseConnection = true });
        using var client = ModbusProbeClient.Connect(library, framing, slave.Endpoint, ModbusProbeOptions.Default);

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"{library} {framing} 对端关闭：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");

        Assert.IsType(expectedType, exception);
        Assert.Contains(expectedMessage, exception!.Message, StringComparison.Ordinal);
        Assert.True(elapsed < ReadTimeout, $"用了 {elapsed}，应在读取超时之前报错。");
    }

    [Fact]
    public async Task FluentModbusRtuOverTcpSpinsOnAClosedConnectionUntilReleased()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp, _ => new SlaveReply { CloseConnection = true });
        var client = ModbusProbeClient.Connect(ModbusLibrary.FluentModbus, ModbusFraming.RtuOverTcp, slave.Endpoint, ModbusProbeOptions.Default);

        // 已记录的缺陷：读到 0 字节后继续循环，读取超时（300 毫秒）不起作用。
        var reading = ReadOnDedicatedThread(client, 0);
        await Task.WhenAny(reading, Task.Delay(HangObservation, cancellationToken));
        Assert.False(reading.IsCompleted, $"连接已关闭、超过读取超时 {HangObservation}，读取仍未结束。");

        client.Dispose();
        var exception = await Record.ExceptionAsync(() => reading.WaitAsync(WaitLimit, cancellationToken));
        output.WriteLine($"释放连接后结束：{Describe(exception)}");
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task FluentModbusRtuOverTcpAsyncSpinsOnAClosedConnectionUntilCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp, _ => new SlaveReply { CloseConnection = true });
        using var client = ModbusProbeClient.Connect(ModbusLibrary.FluentModbus, ModbusFraming.RtuOverTcp, slave.Endpoint, ModbusProbeOptions.Default);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 在线程池线程上调用：异步读取可能一直不让出线程，直接在测试线程上调用会使后面的取消永远执行不到。
        var reading = Task.Run(() => client.ReadAsync(1, 0, 2, stop.Token), cancellationToken);
        await Task.WhenAny(reading, Task.Delay(HangObservation, cancellationToken));
        Assert.False(reading.IsCompleted, $"连接已关闭、超过读取超时 {HangObservation}，异步读取仍未结束。");

        await stop.CancelAsync();
        var exception = await Record.ExceptionAsync(() => reading.WaitAsync(WaitLimit, cancellationToken));
        output.WriteLine($"取消后结束：{Describe(exception)}");
        Assert.NotNull(exception);
    }

    public static TheoryData<ModbusLibrary, ModbusFraming, Type, string> ClosingCombinations() => new()
    {
        { ModbusLibrary.NModbus, ModbusFraming.Tcp, typeof(IOException), "Read resulted in 0 bytes returned" },
        { ModbusLibrary.NModbus, ModbusFraming.RtuOverTcp, typeof(IOException), "Read resulted in 0 bytes returned" },
        { ModbusLibrary.FluentModbus, ModbusFraming.Tcp, typeof(InvalidOperationException), "connection closed unexpectedly" },
    };
}

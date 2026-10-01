using System.Diagnostics;
using FluentModbus;
using NModbus;
using static TenonEdge.IntegrationTests.Probes.Modbus.ModbusProbe;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>
/// T0-12 Modbus 候选库比较（V-05 比较部分、FS-20 的库层基础，验证代码，不是正式适配器）：读取阻塞时如何停止，以及从站忙的处理。
/// <list type="bullet">
/// <item>同步读取阻塞时（读取超时为默认的无限），释放连接能在 1 秒内打断两个库的读取：这是“停止时释放连接打断阻塞读取”（总设计 §6.1、§8.1）的前提。</item>
/// <item>FluentModbus 的异步读取接受取消令牌，取消同时关闭连接；NModbus 的异步读取不接受取消令牌，只能释放连接。</item>
/// <item>从站忙：NModbus 默认无限重试，读取不返回；SlaveBusyUsesRetryCount 才使它受重试次数约束。FluentModbus 直接抛出异常。</item>
/// </list>
/// </summary>
[Trait("Category", "ModbusProbe")]
public sealed class ModbusStopAndBusyComparisonTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AllCombinations), MemberType = typeof(ModbusProbe))]
    public async Task ReleasingTheConnectionInterruptsABlockedSynchronousRead(ModbusLibrary library, ModbusFraming framing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(framing, _ => new SlaveReply { Silent = true });
        var client = ModbusProbeClient.Connect(library, framing, slave.Endpoint, ModbusProbeOptions.Default with { ReadTimeout = Timeout.InfiniteTimeSpan });

        var reading = ReadOnDedicatedThread(client, 0);
        await slave.WaitForRequestsAsync(1, WaitLimit, cancellationToken);
        await Task.WhenAny(reading, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
        Assert.False(reading.IsCompleted, "读取超时为无限时，没有应答的读取应一直阻塞。");

        var stopwatch = Stopwatch.StartNew();
        client.Dispose();
        var exception = await Record.ExceptionAsync(() => reading.WaitAsync(WaitLimit, cancellationToken));
        stopwatch.Stop();
        output.WriteLine($"{library} {framing}：释放连接后 {stopwatch.Elapsed.TotalMilliseconds:F0} ms 结束，{Describe(exception)}");

        Assert.NotNull(exception);
        Assert.IsNotType<TimeoutException>(exception);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"释放连接后 {stopwatch.Elapsed} 才结束。");
    }

    [Theory]
    [InlineData(ModbusFraming.Tcp)]
    [InlineData(ModbusFraming.RtuOverTcp)]
    public async Task FluentModbusCancelsAnAsyncReadAndClosesTheConnection(ModbusFraming framing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(framing, request => request.Sequence == 1 ? new SlaveReply { Silent = true } : SlaveReply.Normal);
        using var client = FluentModbusProbeClient.Connect(framing, slave.Endpoint, ModbusProbeOptions.Default with { ReadTimeout = Timeout.InfiniteTimeSpan });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reading = client.ReadAsync(1, 0, 2, stop.Token);
        await slave.WaitForRequestsAsync(1, WaitLimit, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await stop.CancelAsync();
        var exception = await Record.ExceptionAsync(() => reading.WaitAsync(WaitLimit, cancellationToken));
        stopwatch.Stop();
        var next = await Record.ExceptionAsync(() => client.ReadAsync(1, 0, 2, cancellationToken));
        output.WriteLine($"FluentModbus {framing}：取消后 {stopwatch.Elapsed.TotalMilliseconds:F0} ms 结束，{Describe(exception)}；下一次读取：{Describe(next)}");

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"取消后 {stopwatch.Elapsed} 才结束。");
        Assert.IsType<ObjectDisposedException>(next);
    }

    [Fact]
    public async Task NModbusAsyncReadIgnoresCancellationAndEndsOnlyWhenTheConnectionIsReleased()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        var client = ModbusProbeClient.Connect(ModbusLibrary.NModbus, ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default with { ReadTimeout = Timeout.InfiniteTimeSpan });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 异步方法没有取消令牌参数：传入的令牌只是被封装忽略；读取在线程池线程上同步阻塞（Task.Factory.StartNew）。
        var reading = client.ReadAsync(1, 0, 2, stop.Token);
        await slave.WaitForRequestsAsync(1, WaitLimit, cancellationToken);
        await stop.CancelAsync();
        await Task.WhenAny(reading, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
        Assert.False(reading.IsCompleted, "取消之后读取仍在等待应答。");

        client.Dispose();
        var exception = await Record.ExceptionAsync(() => reading.WaitAsync(WaitLimit, cancellationToken));
        output.WriteLine($"NModbus 异步：释放连接后结束，{Describe(exception)}");
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task NModbusRetriesASlaveBusyResponseIndefinitelyByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { ExceptionCode = 6 });
        var client = ModbusProbeClient.Connect(ModbusLibrary.NModbus, ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default);

        // 重试次数为 0、读取超时 300 毫秒，但从站忙不计入重试：每 250 毫秒重发一次，读取一直不返回。
        var reading = ReadOnDedicatedThread(client, 0);
        await Task.WhenAny(reading, Task.Delay(HangObservation, cancellationToken));
        var requests = slave.RequestCount;
        output.WriteLine($"NModbus 从站忙：{HangObservation.TotalSeconds} 秒内重发 {requests} 次，读取{(reading.IsCompleted ? "已" : "仍未")}返回");
        Assert.False(reading.IsCompleted);
        Assert.True(requests >= 4, $"只收到 {requests} 个请求。");

        client.Dispose();
        await Record.ExceptionAsync(() => reading.WaitAsync(WaitLimit, cancellationToken));
    }

    [Fact]
    public async Task NModbusBoundsSlaveBusyRetriesOnlyWhenTold()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { ExceptionCode = 6 });
        using var client = ModbusProbeClient.Connect(
            ModbusLibrary.NModbus,
            ModbusFraming.Tcp,
            slave.Endpoint,
            ModbusProbeOptions.Default with { Retries = 1, SlaveBusyUsesRetryCount = true });

        var (exception, elapsed) = MeasureRead(() => client.Read(1, 0, 2));
        output.WriteLine($"NModbus 从站忙计入重试：{elapsed.TotalMilliseconds:F0} ms，{Describe(exception)}");

        Assert.Equal(SlaveExceptionCodes.SlaveDeviceBusy, Assert.IsType<SlaveException>(exception).SlaveExceptionCode);
        Assert.Equal(2, slave.RequestCount);
    }

    [Theory]
    [InlineData(ModbusFraming.Tcp)]
    [InlineData(ModbusFraming.RtuOverTcp)]
    public async Task FluentModbusReportsSlaveBusyImmediately(ModbusFraming framing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = ModbusTestSlave.Start(framing, _ => new SlaveReply { ExceptionCode = 6 });
        using var client = ModbusProbeClient.Connect(ModbusLibrary.FluentModbus, framing, slave.Endpoint, ModbusProbeOptions.Default);

        var exception = Record.Exception(() => client.Read(1, 0, 2));
        var asyncException = await Record.ExceptionAsync(() => client.ReadAsync(1, 0, 2, cancellationToken));
        output.WriteLine($"FluentModbus {framing} 从站忙：{Describe(exception)}");

        Assert.Equal(ModbusExceptionCode.ServerDeviceBusy, Assert.IsType<ModbusException>(exception).ExceptionCode);
        Assert.Equal(ModbusExceptionCode.ServerDeviceBusy, Assert.IsType<ModbusException>(asyncException).ExceptionCode);
        Assert.Equal(2, slave.RequestCount);
    }
}

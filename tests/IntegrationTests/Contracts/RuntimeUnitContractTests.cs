using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Contracts;

/// <summary>
/// 运行单元契约（T0-10，T1-01 改由运行时执行；总设计 §7.4 的激活边界与单实例规则、§8.1、§11）：用测试适配器经真实的运行时验证——
/// 准备阶段只构建，不连接、不采集；激活后才连接并生成消息；配置问题在构建时作为结构性失败报告；
/// 停止时单元释放连接，打断不响应取消的阻塞读取；没有激活就放弃的计划不连接设备。
/// 输入单元的上下文只含进程级对象（时钟、时钟状态、读取状态与有界通道），单元因此拿不到请求作用域中的服务，这一点由契约的形状保证。
/// </summary>
public sealed class RuntimeUnitContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // 构建之后观察这么久，排除“稍后才在后台连接或采集”的情况。
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task PreparingDoesNotConnectOrCollectUntilThePlanIsActivated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        using var device = StartDevice(out var port);
        await using var runtime = RuntimeTesting.CreateRuntime([new TcpLineInputFactory()], directory.Path);
        var plan = new RuntimePlan(1, [Unit("meter-1/line", port), Unit("meter-2/line", port)]);

        var prepared = runtime.Prepare(plan);
        Assert.Empty(prepared.Failures);
        var firstConnection = device.AcceptSocketAsync(cancellationToken).AsTask();
        await Task.WhenAny(firstConnection, Task.Delay(QuietPeriod, cancellationToken));
        Assert.False(firstConnection.IsCompleted, "准备阶段不应连接设备。");
        Assert.Null(runtime.ActiveRevision);

        await runtime.ActivateAsync(prepared);
        using var first = await firstConnection.WaitAsync(Timeout, cancellationToken);
        using var second = await device.AcceptSocketAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
        await first.SendAsync("42\n"u8.ToArray(), cancellationToken);
        await second.SendAsync("43\n"u8.ToArray(), cancellationToken);
        var messages = new[]
        {
            await ReadAsync(runtime, "meter-1/line"),
            await ReadAsync(runtime, "meter-2/line"),
        };

        Assert.Equal(1, runtime.ActiveRevision);
        Assert.Equal(["meter-1", "meter-2"], messages.Select(message => message.DeviceKey));
        Assert.All(messages, message =>
        {
            Assert.Equal("line", message.InputId);
            Assert.Equal(TimeSource.Gateway, message.TimeSource);
            Assert.Equal(message.ReceivedAt, message.CollectedAt);
            Assert.Empty(message.Quality);
        });
        // 两个单元连接的先后不确定，只核对两条消息的值。
        Assert.Equal(["42", "43"], messages.Select(message => message.Values[TcpLineInput.ValueField].ToString()).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{"port":5020}""")] // 缺少地址
    [InlineData("""{"address":"device.local","port":5020}""")] // 主机名须解析，不能放在构建阶段
    [InlineData("""{"address":"127.0.0.1","port":0}""")]
    [InlineData("""{"address":"127.0.0.1","port":"5020"}""")]
    public async Task ConfigurationProblemsAreStructuralBuildFailures(string json)
    {
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new TcpLineInputFactory()], directory.Path);
        using var document = JsonDocument.Parse(json);
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create(TcpLineInputFactory.UnitKind, "meter-1/line"), document.RootElement);

        await using var prepared = runtime.Prepare(new RuntimePlan(1, [unit]));

        var failure = Assert.Single(prepared.Failures);
        Assert.Equal(unit.Id, failure.Id);
        Assert.Contains(unit.Id.ToString(), failure.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("input.modbus", "meter-1/line", "没有注册单元种类")] // 缺少模块
    [InlineData("output.mqtt", "broker-1", "未注册输出模块")]
    [InlineData(TcpLineInputFactory.UnitKind, "meter-1", "deviceKey/inputKey")]
    public async Task UnknownKindsAndMalformedInputKeysAreStructuralBuildFailures(string kind, string key, string expected)
    {
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new TcpLineInputFactory()], directory.Path);
        using var document = JsonDocument.Parse("""{"address":"127.0.0.1","port":5020}""");

        await using var prepared = runtime.Prepare(new RuntimePlan(1, [new RuntimeUnitPlan(RuntimeUnitId.Create(kind, key), document.RootElement)]));

        Assert.Contains(expected, Assert.Single(prepared.Failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingReleasesTheConnectionToInterruptABlockedRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        using var device = StartDevice(out var port);
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([new TcpLineInputFactory()], directory.Path, events);
        await runtime.ActivateAsync(runtime.Prepare(new RuntimePlan(1, [Unit("meter-1/line", port)])));
        using var connection = await device.AcceptSocketAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
        await connection.SendAsync("1\n"u8.ToArray(), cancellationToken);
        await ReadAsync(runtime, "meter-1/line");

        // 单元已在循环中，下一次读取阻塞且不响应取消；停止须释放连接来打断它。
        var stopwatch = Stopwatch.StartNew();
        await runtime.StopAsync(cancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"停止用了 {stopwatch.Elapsed}。");
        var status = Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == TcpLineInputFactory.UnitKind);
        Assert.Equal(RuntimeUnitState.Stopped, status.State);
        Assert.Equal(0, status.RestartCount);
        Assert.Empty(events.Events);
        Assert.True(await IsClosedByPeerAsync(connection, cancellationToken), "设备一侧应看到连接已被释放。");
    }

    [Fact]
    public async Task APlanThatIsNotActivatedNeverConnects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        using var device = StartDevice(out var port);
        await using var runtime = RuntimeTesting.CreateRuntime([new TcpLineInputFactory()], directory.Path);

        // 准备之后放弃（例如应用在准备阶段失败）：已构建的实例被释放，不会连接设备。
        var prepared = runtime.Prepare(new RuntimePlan(1, [Unit("meter-1/line", port)]));
        await prepared.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateAsync(prepared));
        Assert.Null(runtime.ActiveRevision);
        var connection = device.AcceptSocketAsync(cancellationToken).AsTask();
        await Task.WhenAny(connection, Task.Delay(QuietPeriod, cancellationToken));
        Assert.False(connection.IsCompleted, "放弃的计划不应连接设备。");
    }

    [Fact]
    public async Task AUnitAskedToRunWithACancelledTokenReturnsWithoutConnecting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = StartDevice(out var port);
        var channel = Channel.CreateBounded<EdgeMessage>(10);
        var context = new InputUnitContext("meter-1", "line", TimeProvider.System, new FixedClockSyncStatus(ClockSyncState.Unknown), new RecordingReads(), channel.Writer);
        await using var unit = new TcpLineInputFactory().Build(Unit("meter-1/line", port), context);

        await unit.RunAsync(new CancellationToken(canceled: true)).WaitAsync(Timeout, cancellationToken);

        var connection = device.AcceptSocketAsync(cancellationToken).AsTask();
        await Task.WhenAny(connection, Task.Delay(QuietPeriod, cancellationToken));
        Assert.False(connection.IsCompleted, "已请求停止的单元不应连接设备。");
    }

    /// <summary>
    /// 释放仍有读取未完成的套接字时，.NET 在 Linux 上中止连接（RST），设备一侧收到“连接被重置”；
    /// 没有未完成的读取时则正常关闭，设备一侧读到 0 字节。两者都说明单元已释放连接。
    /// </summary>
    private static async Task<bool> IsClosedByPeerAsync(Socket connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16];
        try
        {
            return await connection.ReceiveAsync(buffer, cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken) == 0;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
        {
            return true;
        }
    }

    private static TcpListener StartDevice(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static RuntimeUnitPlan Unit(string key, int port)
    {
        using var document = JsonDocument.Parse($$"""{"address":"127.0.0.1","port":{{port}}}""");
        return new RuntimeUnitPlan(RuntimeUnitId.Create(TcpLineInputFactory.UnitKind, key), document.RootElement);
    }

    private static async Task<EdgeMessage> ReadAsync(RuntimeHost runtime, string key)
    {
        var channel = runtime.GetInputChannel(RuntimeUnitId.Create(TcpLineInputFactory.UnitKind, key))
            ?? throw new InvalidOperationException($"{key} 没有输入通道。");
        return await channel.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingReads : IInputReadReporter
    {
        public void ReadCompleted(DateTimeOffset completedAt, int failedFieldCount)
        {
        }

        public void ReadFailed(DateTimeOffset completedAt, string reason)
        {
        }

        public void CycleSkipped(DateTimeOffset skippedAt)
        {
        }
    }
}

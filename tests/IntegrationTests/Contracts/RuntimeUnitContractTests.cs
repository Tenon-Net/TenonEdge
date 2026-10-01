using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.IntegrationTests.Contracts;

/// <summary>
/// 运行单元契约（T0-10；总设计 §7.4 的激活边界与单实例规则、§8.1、§11）：用测试适配器验证——
/// 构建只做本地计算，不连接、不采集；启动后才连接并生成消息；配置问题在构建时作为结构性失败报告；
/// 停止时释放连接，打断不响应取消的阻塞读取；只能启动一次，停止之后不能再启动。
/// 契约的上下文只含进程级对象（TimeProvider 与有界通道），单元因此拿不到请求作用域中的服务，这一点由契约的形状保证，不另写测试。
/// 运行时宿主按计划“先全部构建、提交之后再启动”的完整流程在 T1-01 实现和测试。
/// </summary>
public sealed class RuntimeUnitContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // 构建之后观察这么久，排除“稍后才在后台连接或采集”的情况。
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task BuildingDoesNotConnectOrCollectUntilTheUnitIsStarted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = StartDevice(out var port);
        var channel = CreateChannel();
        var plan = new RuntimePlan(1, [Unit("meter-1", port), Unit("meter-2", port)]);
        var factory = new TcpLineInputFactory();

        var units = plan.Units.Select(unit => factory.Build(unit, Context(channel))).ToList();
        try
        {
            var firstConnection = device.AcceptSocketAsync(cancellationToken).AsTask();
            await Task.WhenAny(firstConnection, Task.Delay(QuietPeriod, cancellationToken));
            Assert.False(firstConnection.IsCompleted, "构建阶段不应连接设备。");
            Assert.False(channel.Reader.TryRead(out _), "构建阶段不应生成消息。");

            foreach (var unit in units)
            {
                await unit.StartAsync(cancellationToken);
            }

            using var first = await firstConnection.WaitAsync(Timeout, cancellationToken);
            using var second = await device.AcceptSocketAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
            await first.SendAsync("42\n"u8.ToArray(), cancellationToken);
            await second.SendAsync("43\n"u8.ToArray(), cancellationToken);
            EdgeMessage[] messages = [await ReadAsync(channel), await ReadAsync(channel)];

            Assert.Equal(["meter-1", "meter-2"], messages.Select(message => message.DeviceKey).Order(StringComparer.Ordinal));
            Assert.Equal(["42", "43"], messages.Select(message => message.Values[TcpLineInput.ValueField].ToString()).Order(StringComparer.Ordinal));
            Assert.All(messages, message =>
            {
                Assert.Equal(TimeSource.Gateway, message.TimeSource);
                Assert.Equal(message.ReceivedAt, message.CollectedAt);
                Assert.Empty(message.Quality);
            });
        }
        finally
        {
            foreach (var unit in units)
            {
                await unit.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData("""{"port":5020,"deviceKey":"meter-1"}""")] // 缺少地址
    [InlineData("""{"address":"device.local","port":5020,"deviceKey":"meter-1"}""")] // 主机名须解析，不能放在构建阶段
    [InlineData("""{"address":"127.0.0.1","port":0,"deviceKey":"meter-1"}""")]
    [InlineData("""{"address":"127.0.0.1","port":"5020","deviceKey":"meter-1"}""")]
    [InlineData("""{"address":"127.0.0.1","port":5020}""")]
    public void ConfigurationProblemsAreStructuralBuildFailures(string json)
    {
        using var document = JsonDocument.Parse(json);
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create(TcpLineInputFactory.UnitKind, "meter-1"), document.RootElement);

        var exception = Assert.Throws<RuntimeUnitBuildException>(() => new TcpLineInputFactory().Build(unit, Context(CreateChannel())));

        Assert.Equal(unit.Id, exception.UnitId);
    }

    [Fact]
    public void AUnitOfAnotherKindIsRejected()
    {
        using var document = JsonDocument.Parse("""{"address":"127.0.0.1","port":5020,"deviceKey":"meter-1"}""");
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create("input.modbus", "meter-1"), document.RootElement);

        Assert.Throws<RuntimeUnitBuildException>(() => new TcpLineInputFactory().Build(unit, Context(CreateChannel())));
    }

    [Fact]
    public async Task StoppingReleasesTheConnectionToInterruptABlockedRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = StartDevice(out var port);
        var channel = CreateChannel();
        var unit = (TcpLineInput)new TcpLineInputFactory().Build(Unit("meter-1", port), Context(channel));
        try
        {
            await unit.StartAsync(cancellationToken);
            using var connection = await device.AcceptSocketAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
            await connection.SendAsync("1\n"u8.ToArray(), cancellationToken);
            await ReadAsync(channel);

            // 单元已在循环中，下一次读取阻塞且不响应取消；停止须释放连接来打断它。
            using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopTimeout.CancelAfter(Timeout);
            var stopwatch = Stopwatch.StartNew();
            await unit.StopAsync(stopTimeout.Token);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"停止用了 {stopwatch.Elapsed}。");
            Assert.Null(unit.LastError);
            Assert.True(await IsClosedByPeerAsync(connection, cancellationToken), "设备一侧应看到连接已被释放。");
        }
        finally
        {
            await unit.DisposeAsync();
        }
    }

    [Fact]
    public async Task AUnitCanOnlyBeStartedOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = StartDevice(out var port);
        await using var unit = new TcpLineInputFactory().Build(Unit("meter-1", port), Context(CreateChannel()));

        await unit.StartAsync(cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.StartAsync(cancellationToken));
    }

    [Fact]
    public async Task AUnitStoppedBeforeItStartsCannotBeStarted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = StartDevice(out var port);
        await using var unit = new TcpLineInputFactory().Build(Unit("meter-1", port), Context(CreateChannel()));

        // 启动之前就请求停止（例如应用在准备阶段失败）：停止立即完成，之后不能再启动，也不会连接设备。
        await unit.StopAsync(cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.StartAsync(cancellationToken));
        var connection = device.AcceptSocketAsync(cancellationToken).AsTask();
        await Task.WhenAny(connection, Task.Delay(QuietPeriod, cancellationToken));
        Assert.False(connection.IsCompleted, "已停止的单元不应连接设备。");
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

    private static Channel<EdgeMessage> CreateChannel() =>
        Channel.CreateBounded<EdgeMessage>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    private static RuntimeUnitContext Context(Channel<EdgeMessage> channel) => new(TimeProvider.System, channel.Writer);

    private static RuntimeUnitPlan Unit(string key, int port)
    {
        using var document = JsonDocument.Parse($$"""{"address":"127.0.0.1","port":{{port}},"deviceKey":"{{key}}"}""");
        return new RuntimeUnitPlan(RuntimeUnitId.Create(TcpLineInputFactory.UnitKind, key), document.RootElement);
    }

    private static async Task<EdgeMessage> ReadAsync(Channel<EdgeMessage> channel) =>
        await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
}

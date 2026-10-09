using System.Net.Sockets;
using static TenonEdge.IntegrationTests.Probes.Modbus.ModbusProbe;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>
/// T0-12 Modbus 候选库比较（V-05 比较部分、FS-19 的库层基础，验证代码，不是正式适配器）：超时之后才到达的迟到应答、较旧事务号与从站地址校验。
/// 场景：第 1 个请求的应答在客户端超时之后才到达；之后的请求读到的是排在前面的旧应答。
/// <list type="bullet">
/// <item>Modbus TCP：NModbus 校验事务号，默认不跳过旧帧，于是每个后续请求都读到上一个请求的应答而失败；设置 RetryOnOldResponseThreshold 后跳过旧帧恢复正常。
/// FluentModbus 不校验事务号与单元标识，把旧应答当作本次结果返回，错位一直持续且没有任何错误。</item>
/// <item>RTU over TCP：帧中没有事务号，同一从站、同功能码、同长度的迟到应答能通过全部校验，两个库都把它当作下一个请求的结果；
/// 发送前清空缓冲对网络流无效（NModbus）或根本没有（FluentModbus）。适配层在超时后按静默间隔排空残留字节即可避免（总设计 §6.1）。</item>
/// </list>
/// 用例中的寄存器值为 0x1000 加地址，读到哪个地址的值一目了然。
/// </summary>
// 此类同步库调用与进程内异步从站共同使用线程池；300 ms 的协议观察窗口
// 不应同时承担其他集成测试的调度压力。只隔离本类，不改变断言、超时或其他测试并行性。
[Collection("Timing-sensitive Modbus comparison")]
[Trait("Category", "ModbusProbe")]
public sealed class ModbusStaleResponseComparisonTests(ITestOutputHelper output)
{
    // 迟到场景使用较长的超时，留出时间让旧应答先于下一个请求到达、再让下一个应答在其后单独到达。
    private static readonly ModbusProbeOptions StaleOptions = ModbusProbeOptions.Default with { ReadTimeout = TimeSpan.FromMilliseconds(500) };
    private static readonly TimeSpan StaleDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan PauseAfterLateResponse = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task NModbusFailsEveryLaterRequestAfterOneLateTcpResponseByDefault()
    {
        await using var slave = StartWithOneLateResponse(ModbusFraming.Tcp);
        using var client = NModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, StaleOptions);
        await TimeOutTheFirstRequestAsync(client, slave);

        var failures = new List<Exception?>();
        foreach (var address in new ushort[] { 50, 60, 70 })
        {
            failures.Add(Record.Exception(() => client.Read(1, address, 2)));
        }

        output.WriteLine(string.Join(Environment.NewLine, failures.Select(Describe)));

        // 每次都读到上一个请求的应答：事务号不符，抛出 IOException。不重连就一直错位。
        Assert.All(failures, failure =>
            Assert.Contains("not of expected transaction ID", Assert.IsType<IOException>(failure).Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NModbusSkipsAnOlderTransactionWhenAThresholdIsSet()
    {
        await using var slave = StartWithOneLateResponse(ModbusFraming.Tcp);
        using var client = NModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, StaleOptions with { RetryOnOldResponseThreshold = 5 });
        await TimeOutTheFirstRequestAsync(client, slave);

        Assert.Equal(ModbusTestSlave.RegisterValues(50, 2), client.Read(1, 50, 2));
        Assert.Equal(ModbusTestSlave.RegisterValues(60, 2), client.Read(1, 60, 2));
    }

    [Fact]
    public async Task FluentModbusReturnsTheLateTcpResponseAsTheNextResult()
    {
        await using var slave = StartWithOneLateResponse(ModbusFraming.Tcp);
        using var client = FluentModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, StaleOptions);
        await TimeOutTheFirstRequestAsync(client, slave);

        var second = client.Read(1, 50, 2);
        var third = client.Read(1, 60, 2);
        output.WriteLine($"请求地址 50 得到 {Format(second)}；请求地址 60 得到 {Format(third)}");

        // 已记录的缺陷：不校验事务号，地址 50 的请求拿到地址 0 的值，地址 60 的请求拿到地址 50 的值，且没有任何错误。
        // 第三次读取时下一个应答往往紧随其后、被同一次读取收下，返回的数组因此可能更长（见下一个用例）。
        Assert.Equal(ModbusTestSlave.RegisterValues(0, 2), second);
        Assert.Equal(ModbusTestSlave.RegisterValues(50, 2), third[..2]);
    }

    [Fact]
    public async Task FluentModbusReturnsTheBytesOfAFollowingTcpFrameAsRegisterData()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => request.Sequence == 1 ? new SlaveReply { Duplicate = true } : SlaveReply.Normal);
        using var client = FluentModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default);

        var first = client.Read(1, 0, 2);
        var second = client.Read(1, 50, 2);
        output.WriteLine($"请求 2 个寄存器得到 {first.Length} 个：{Format(first)}；下一次读取得到 {Format(second)}");

        // 已记录的缺陷：返回的数据按本次读到的全部字节截取，不按应答帧的长度；随后的整帧（13 字节）被当作寄存器数据返回，下一帧因此丢失。
        Assert.Equal(ModbusTestSlave.RegisterValues(0, 2), first[..2]);
        Assert.Equal(2 + (13 / 2), first.Length);
        Assert.Equal(ModbusTestSlave.RegisterValues(50, 2), second);
    }

    [Fact]
    public async Task NModbusLeavesAFollowingTcpFrameForTheNextRequest()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => request.Sequence == 1 ? new SlaveReply { Duplicate = true } : SlaveReply.Normal);
        using var client = NModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default);

        // NModbus 按 MBAP 的长度逐帧读取：本次结果正确，重复的帧留给下一个请求，默认设置下因事务号不符而失败。
        Assert.Equal(ModbusTestSlave.RegisterValues(0, 2), client.Read(1, 0, 2));
        var exception = Record.Exception(() => client.Read(1, 50, 2));
        output.WriteLine(Describe(exception));
        Assert.Contains("not of expected transaction ID", Assert.IsType<IOException>(exception).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NModbusRejectsATcpResponseFromAnotherUnit()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => new SlaveReply { Unit = (byte)(request.Unit + 1) });
        using var client = NModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default);

        var exception = Record.Exception(() => client.Read(1, 0, 2));

        Assert.Contains("slave address does not match", Assert.IsType<IOException>(exception).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FluentModbusAcceptsATcpResponseFromAnotherUnit()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => new SlaveReply { Unit = (byte)(request.Unit + 1) });
        using var client = FluentModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default);

        // 已记录的缺陷：应答中的单元标识被读出后丢弃。
        Assert.Equal(ModbusTestSlave.RegisterValues(0, 2), client.Read(1, 0, 2));
    }

    [Theory]
    [InlineData(ModbusLibrary.NModbus)]
    [InlineData(ModbusLibrary.FluentModbus)]
    public async Task BothLibrariesTakeALateRtuResponseAsTheNextResult(ModbusLibrary library)
    {
        await using var slave = StartWithOneLateResponse(ModbusFraming.RtuOverTcp);
        using var client = ModbusProbeClient.Connect(library, ModbusFraming.RtuOverTcp, slave.Endpoint, StaleOptions);
        await TimeOutTheFirstRequestAsync(client, slave);

        var second = client.Read(1, 50, 2);
        output.WriteLine($"{library}：请求地址 50 得到 {Format(second)}");

        // RTU 帧没有事务号，迟到应答的从站地址、功能码、字节数和 CRC 都正确，只能由适配层的静默间隔排除。
        Assert.Equal(ModbusTestSlave.RegisterValues(0, 2), second);
    }

    [Fact]
    public async Task DrainingForASilenceIntervalAfterATimeoutRestoresRtuReads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var slave = StartWithOneLateResponse(ModbusFraming.RtuOverTcp);
        using var client = NModbusProbeClient.Connect(ModbusFraming.RtuOverTcp, slave.Endpoint, StaleOptions);
        await TimeOutTheFirstRequestAsync(client, slave);

        // 适配层的做法（总设计 §6.1）：超时后进入静默间隔，期间收到的字节全部丢弃，静默间隔内不再有字节到达才发送下一个请求。
        var discarded = await DrainUntilSilentAsync(client.Connection.Client, TimeSpan.FromMilliseconds(300), cancellationToken);
        output.WriteLine($"静默间隔内丢弃 {discarded} 个字节");

        Assert.Equal(9, discarded); // 地址 0 的迟到应答：从站地址、功能码、字节数、4 个数据字节、2 个 CRC 字节
        Assert.Equal(ModbusTestSlave.RegisterValues(50, 2), client.Read(1, 50, 2));
        Assert.Equal(ModbusTestSlave.RegisterValues(60, 2), client.Read(1, 60, 2));
    }

    /// <summary>第 1 个请求在客户端超时之后才应答，应答后从站暂停一会儿再处理下一个请求。</summary>
    private static ModbusTestSlave StartWithOneLateResponse(ModbusFraming framing) =>
        ModbusTestSlave.Start(framing, request => request.Sequence == 1
            ? new SlaveReply { Delay = StaleDelay, PauseAfter = PauseAfterLateResponse }
            : SlaveReply.Normal);

    /// <summary>读取地址 0 并超时，再等到迟到的应答已经到达客户端的接收缓冲。</summary>
    private static async Task TimeOutTheFirstRequestAsync(ModbusProbeClient client, ModbusTestSlave slave)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.IsType<IOException>(Record.Exception(() => client.Read(1, 0, 2)));
        await slave.WaitForResponsesAsync(1, WaitLimit, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
    }

    /// <summary>读取并丢弃到达的字节，直到连续 <paramref name="silence"/> 没有字节到达。</summary>
    private static async Task<int> DrainUntilSilentAsync(Socket socket, TimeSpan silence, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        var discarded = 0;
        while (true)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(silence);
            try
            {
                var received = await socket.ReceiveAsync(buffer, SocketFlags.None, window.Token);
                if (received == 0)
                {
                    return discarded;
                }

                discarded += received;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return discarded;
            }
        }
    }

    private static string Format(ushort[] registers) => string.Join(", ", registers.Select(register => $"0x{register:X4}"));
}

[CollectionDefinition("Timing-sensitive Modbus comparison", DisableParallelization = true)]
public sealed class ModbusComparisonTimingScope;

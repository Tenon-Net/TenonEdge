using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

/// <summary>
/// 模拟输入在运行时中的行为（T1-02，GW-07；总设计 §6.1、§6.4、§8.1、§8.7），使用可控时钟：
/// 按周期读取并生成符合契约的消息；时间来源随时钟同步状态变化；通道满时等到本周期截止仍无空位就跳过并计 overrun，
/// 内存中最多只有通道容量那么多条；连接错误不生成消息，连续 3 个周期判为离线；同一种子在运行时中得到同一序列。
/// </summary>
public sealed class SimulatorInputTests
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EachPeriodProducesOneMessageThatFollowsTheContract()
    {
        await using var harness = Harness.Start("""{"seed":42}""");

        var messages = new List<EdgeMessage> { await harness.ReadAsync() };
        for (var cycle = 1; cycle < 4; cycle++)
        {
            harness.Time.Advance(Period);
            messages.Add(await harness.ReadAsync());
        }

        Assert.Equal([Start, Start + Period, Start + (2 * Period), Start + (3 * Period)], messages.Select(message => message.CollectedAt));
        Assert.All(messages, message =>
        {
            Assert.Equal("meter-001", message.DeviceKey);
            Assert.Equal("simulated", message.InputId);
            Assert.Equal(message.ReceivedAt, message.CollectedAt);
            Assert.Equal(TimeSource.Gateway, message.TimeSource);
            Assert.Equal(7, message.SourceMessageId.Version);
            Assert.Equal(["current", "energy", "frequency", "power", "voltage"], message.Values.Keys);
        });
        Assert.Equal(4, messages.Select(message => message.SourceMessageId).Distinct().Count());
        Assert.Equal(4, harness.Reads.Snapshot().ReadsCompleted);
        Assert.Equal(InputReadHealth.Normal, harness.Reads.Snapshot().Health);
    }

    [Theory]
    [InlineData(ClockSyncState.Unknown, TimeSource.Gateway)]
    [InlineData(ClockSyncState.Synchronized, TimeSource.Gateway)]
    [InlineData(ClockSyncState.NotSynchronized, TimeSource.GatewayUnsynced)]
    public async Task TheTimeSourceFollowsTheClockState(ClockSyncState state, TimeSource expected)
    {
        await using var harness = Harness.Start("{}", clock: state);

        Assert.Equal(expected, (await harness.ReadAsync()).TimeSource);
    }

    [Fact]
    public async Task AFullChannelSkipsCyclesAndKeepsMemoryBounded()
    {
        const int capacity = 2;
        const int advances = 10;
        await using var harness = Harness.Start("""{"seed":42}""", capacity: capacity);
        await harness.WaitForReadsAsync(1);

        for (var advance = 1; advance <= advances; advance++)
        {
            harness.Time.Advance(Period);
            await harness.WaitForReadsAsync(advance + 1);
        }

        // 周期 0、1 进入通道；之后每个周期都等到下一个周期开始仍无空位，丢弃并计 overrun。
        var snapshot = harness.Reads.Snapshot();
        Assert.Equal(advances - capacity, snapshot.CyclesSkipped);
        Assert.Equal(capacity, harness.Channel.Reader.Count);

        // 取走之后照常进入通道：先是早先的两条，然后是正在等待的当前周期，被跳过的周期不会补出来。
        Assert.Equal(Start, (await harness.ReadAsync()).CollectedAt);
        Assert.Equal(Start + Period, (await harness.ReadAsync()).CollectedAt);
        Assert.Equal(Start + (advances * Period), (await harness.ReadAsync()).CollectedAt);
    }

    [Fact]
    public async Task ALoopHeldUpForSeveralPeriodsSkipsTheMissedCyclesAndCountsThem()
    {
        // 读取之后的处理被拖住 3 个周期（例如进程被暂停）：计时器把错过的周期合并为一次，单元按单调时钟补计 2 个被跳过的周期，
        // 下一次读取在第 3 个周期，不补出错过的读取。
        var time = new FakeTimeProvider(Start);
        var reads = new InputReadStatus("meter-001", "simulated", new RecordingEventSink());
        using var gate = new HoldFirstRead(reads);
        var channel = Channel.CreateBounded<EdgeMessage>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
        var context = new InputUnitContext("meter-001", "simulated", time, new FixedClockSyncStatus(ClockSyncState.Unknown), gate, channel.Writer);
        var unit = new SimulatorInputFactory().Build(Unit("meter-001/simulated", "{}"), context);
        using var stopping = new CancellationTokenSource();
        var run = Task.Run(() => unit.RunAsync(stopping.Token), TestContext.Current.CancellationToken);
        await gate.Held.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        time.Advance(3 * Period);
        gate.Release();

        Assert.Equal(Start, (await ReadAsync(channel.Reader)).CollectedAt);
        Assert.Equal(Start + (3 * Period), (await ReadAsync(channel.Reader)).CollectedAt);
        Assert.Equal(2, reads.Snapshot().CyclesSkipped);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, channel.Reader.Count);

        time.Advance(Period);
        Assert.Equal(Start + (4 * Period), (await ReadAsync(channel.Reader)).CollectedAt);
        Assert.Equal(2, reads.Snapshot().CyclesSkipped);
        await stopping.CancelAsync();
        await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConnectionErrorsProduceNoMessagesAndTheDeviceGoesOfflineAfterThreeCycles()
    {
        await using var harness = Harness.Start("""{"faults":{"connectionError":1}}""");
        await harness.WaitForReadsAsync(1);
        harness.Time.Advance(Period);
        await harness.WaitForReadsAsync(2);
        Assert.Equal(InputReadHealth.Failing, harness.Reads.Snapshot().Health);
        harness.Time.Advance(Period);
        await harness.WaitForReadsAsync(3);

        var snapshot = harness.Reads.Snapshot();
        Assert.Equal(InputReadHealth.Offline, snapshot.Health);
        Assert.Equal(SimulatorInputUnit.ConnectionErrorReason, snapshot.LastError);
        Assert.Null(snapshot.LastSuccessAt);
        Assert.Equal(0, harness.Channel.Reader.Count);
        Assert.Single(harness.Events.OfType(RuntimeEventTypes.DeviceOffline));
    }

    [Fact]
    public async Task StoppingEndsTheLoopWhileItWaitsForTheNextCycleOrForSpace()
    {
        await using var waitingForCycle = Harness.Start("{}");
        await waitingForCycle.ReadAsync();
        await waitingForCycle.StopAsync();

        await using var waitingForSpace = Harness.Start("{}", capacity: 1);
        await waitingForSpace.WaitForReadsAsync(1);
        waitingForSpace.Time.Advance(Period);
        await waitingForSpace.WaitForReadsAsync(2);
        await waitingForSpace.StopAsync();
        Assert.Equal(0, waitingForSpace.Reads.Snapshot().CyclesSkipped);
    }

    [Fact]
    public async Task TheRuntimeReproducesTheSameSequenceForTheSameSeed()
    {
        using var directory = TemporaryDirectory.Create();
        var time = new FakeTimeProvider(Start);
        await using var runtime = RuntimeTesting.CreateRuntime([new SimulatorInputFactory()], directory.Path, time: time);
        var plan = new RuntimePlan(1, [Unit("meter-001/a", """{"seed":7,"faults":{"invalidValue":0.3,"missingField":0.3}}"""), Unit("meter-002/b", """{"seed":7,"faults":{"invalidValue":0.3,"missingField":0.3}}""")]);
        await runtime.ActivateAsync(runtime.Prepare(plan));
        var first = runtime.GetInputChannel(plan.Units[0].Id)!;
        var second = runtime.GetInputChannel(plan.Units[1].Id)!;

        var pairs = new List<(EdgeMessage First, EdgeMessage Second)>();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            pairs.Add((await ReadAsync(first), await ReadAsync(second)));
            time.Advance(Period);
        }

        Assert.All(pairs, pair =>
        {
            Assert.Equal(Describe(pair.First), Describe(pair.Second));
            Assert.NotEqual(pair.First.SourceMessageId, pair.Second.SourceMessageId);
        });
        Assert.Contains(pairs, pair => pair.First.Quality.Count > 0);
        Assert.All(runtime.GetStatus().Units.Where(unit => unit.Reads is not null), unit => Assert.Equal(RuntimeUnitState.Running, unit.State));
    }

    private static string Describe(EdgeMessage message) =>
        string.Join(",", message.Values.Select(field => $"{field.Key}={field.Value}"))
        + "|" + string.Join(",", message.Quality.Select(field => $"{field.Key}={field.Value}"));

    private static RuntimeUnitPlan Unit(string key, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new RuntimeUnitPlan(RuntimeUnitId.Create(SimulatorInputFactory.UnitKind, key), document.RootElement);
    }

    private static async Task<EdgeMessage> ReadAsync(ChannelReader<EdgeMessage> reader) =>
        await reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

    /// <summary>第一次上报读取完成时停住，直到测试放行，用来模拟读取之后的处理被拖住。</summary>
    private sealed class HoldFirstRead(IInputReadReporter inner) : IInputReadReporter, IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _calls;

        public Task Held => _held.Task;

        public void Release() => _release.Set();

        public void ReadCompleted(DateTimeOffset completedAt, int failedFieldCount)
        {
            inner.ReadCompleted(completedAt, failedFieldCount);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _held.TrySetResult();
                _release.Wait(Timeout);
            }
        }

        public void ReadFailed(DateTimeOffset completedAt, string reason) => inner.ReadFailed(completedAt, reason);

        public void CycleSkipped(DateTimeOffset skippedAt) => inner.CycleSkipped(skippedAt);

        public void Dispose() => _release.Dispose();
    }

    /// <summary>直接运行一个模拟输入单元：可控时钟、记录事件的替身与指定容量的通道。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private Task _run = Task.CompletedTask;

        private Harness(int capacity, ClockSyncState clock)
        {
            Channel = System.Threading.Channels.Channel.CreateBounded<EdgeMessage>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
            Reads = new InputReadStatus("meter-001", "simulated", Events);
            Context = new InputUnitContext("meter-001", "simulated", Time, new FixedClockSyncStatus(clock), Reads, Channel.Writer);
        }

        public FakeTimeProvider Time { get; } = new(SimulatorInputTests.Start);

        public RecordingEventSink Events { get; } = new();

        public Channel<EdgeMessage> Channel { get; }

        public InputReadStatus Reads { get; }

        private InputUnitContext Context { get; }

        public static Harness Start(string json, int capacity = 100, ClockSyncState clock = ClockSyncState.Unknown)
        {
            var harness = new Harness(capacity, clock);
            var unit = new SimulatorInputFactory().Build(Unit("meter-001/simulated", json), harness.Context);
            harness._run = Task.Run(() => unit.RunAsync(harness._stopping.Token));
            return harness;
        }

        public Task<EdgeMessage> ReadAsync() => SimulatorInputTests.ReadAsync(Channel.Reader);

        public async Task WaitForReadsAsync(long count)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = Reads.Snapshot();
                if (snapshot.ReadsCompleted + snapshot.ReadsFailed >= count)
                {
                    return;
                }

                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"等待第 {count} 次读取超时：{Reads.Snapshot()}");
        }

        public async Task StopAsync()
        {
            await _stopping.CancelAsync();
            await _run.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _stopping.Dispose();
        }
    }
}

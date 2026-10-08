using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

/// <summary>
/// 监督者（T1-01；总设计 §7.4 单实例规则、§8.1）：单元的异常在单元边界捕获，记录最后错误并按退避重启，不冒泡到宿主；
/// 同一单元同一时刻最多一个实例——上一个实例的循环退出并释放之后才构建下一个，停止超时的单元仍占用其标识。
/// </summary>
public sealed class UnitSupervisorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly RuntimeUnitId UnitId = RuntimeUnitId.Create("input.test", "meter-1/line");

    [Theory]
    [InlineData("run")]
    [InlineData("rebuild")]
    [InlineData("dispose")]
    [InlineData("stop")]
    public async Task UnexpectedExceptionsDoNotLeakToStatusEventsOrLoggerExceptionObjects(string phase)
    {
        const string Marker = "fixture-sensitive-runtime-detail";
        var time = new FakeTimeProvider();
        var events = new RecordingEventSink();
        using var logs = new LogCapture();
        var supervisor = new UnitSupervisor(new RuntimeOptions
        { InitialRestartDelay = TimeSpan.FromMilliseconds(100), MaximumRestartDelay = TimeSpan.FromSeconds(10), RestartJitter = 0 },
            time, events, logs.CreateLogger("supervisor"), random: () => 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = phase == "stop"
            ? new ScriptedUnit(UnitId, async token =>
            {
                await using var callback = token.Register(() => throw new IOException(Marker));
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            })
            : new ScriptedUnit(UnitId, _ => throw new InvalidOperationException(Marker, new IOException(Marker)),
                phase == "dispose" ? () => throw new IOException(Marker) : null);
        IRuntimeUnit Rebuild() => throw new ArgumentException(Marker);
        supervisor.TryStart(initial, Rebuild, reads: null);
        if (phase == "stop")
        { await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken); }
        else
        { await WaitForAsync(supervisor, status => status.RestartCount == 1); }
        if (phase == "rebuild")
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await WaitForAsync(supervisor, status => status.RestartCount == 2);
        }
        await StopAllAsync(supervisor);
        Assert.DoesNotContain(Marker, Status(supervisor).LastError!, StringComparison.Ordinal);
        Assert.NotEmpty(events.Events);
        Assert.All(events.Events, entry => Assert.DoesNotContain(Marker, entry.Description, StringComparison.Ordinal));
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(Marker, entry.Message, StringComparison.Ordinal);
            Assert.Null(entry.ExceptionText);
        });
    }

    [Fact]
    public async Task AFailingUnitIsRestartedAfterBackoffAndTheFailureIsRecorded()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var events = new RecordingEventSink();
        var supervisor = Supervisor(time, events);
        var runs = new ConcurrentQueue<int>();
        var builds = 0;
        IRuntimeUnit Build()
        {
            var number = Interlocked.Increment(ref builds);
            return new ScriptedUnit(UnitId, token =>
            {
                runs.Enqueue(number);
                return number < 3 ? throw new InvalidOperationException($"模拟故障 {number}") : Task.Delay(System.Threading.Timeout.Infinite, token);
            });
        }

        Assert.True(supervisor.TryStart(Build(), Build, reads: null));
        var first = await WaitForAsync(supervisor, status => status.State == RuntimeUnitState.Backoff && status.RestartCount == 1);
        Assert.Equal("运行单元发生未预期错误。", first.LastError);
        Assert.Equal(time.GetUtcNow(), first.LastErrorAt);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMilliseconds(100), first.NextRestartAt);
        var faulted = Assert.Single(events.OfType(RuntimeEventTypes.UnitFaulted));
        Assert.Equal(UnitId.ToString(), faulted.Target);

        // 退避没到期之前不重启。
        time.Advance(TimeSpan.FromMilliseconds(99));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal([1], runs);

        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = await WaitForAsync(supervisor, status => status.RestartCount == 2);

        // 连续第二次故障的等待加倍。
        Assert.Equal(second.LastErrorAt + TimeSpan.FromMilliseconds(200), second.NextRestartAt);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await WaitForAsync(supervisor, _ => runs.Count == 3);
        Assert.Equal([1, 2, 3], runs);
        Assert.Equal(RuntimeUnitState.Running, Status(supervisor).State);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task TheBackoffStartsOverAfterTheUnitRanForTheResetPeriod()
    {
        // 退避上限 10 秒，连续运行满 10 秒后，下一次故障的等待从初始的 100 毫秒重新计算。
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var supervisor = Supervisor(time, new RecordingEventSink());
        var builds = 0;
        IRuntimeUnit Build() => Interlocked.Increment(ref builds) switch
        {
            1 or 2 => new ScriptedUnit(UnitId, _ => throw new InvalidOperationException("模拟故障")),
            3 => new ScriptedUnit(UnitId, async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(11), time, token);
                throw new InvalidOperationException("运行 11 秒后故障");
            }),
            _ => new ScriptedUnit(UnitId, token => Task.Delay(System.Threading.Timeout.Infinite, token)),
        };

        supervisor.TryStart(Build(), Build, reads: null);
        var first = await WaitForAsync(supervisor, status => status.RestartCount == 1);
        Assert.Equal(TimeSpan.FromMilliseconds(100), first.NextRestartAt - first.LastErrorAt);
        time.Advance(TimeSpan.FromMilliseconds(100));
        var second = await WaitForAsync(supervisor, status => status.RestartCount == 2);
        Assert.Equal(TimeSpan.FromMilliseconds(200), second.NextRestartAt - second.LastErrorAt);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await WaitForAsync(supervisor, _ => Volatile.Read(ref builds) >= 3);

        // 第 3 个实例运行满 11 秒后故障：等待回到 100 毫秒，而不是 400 毫秒。
        for (var second2 = 0; second2 < 30 && Status(supervisor).RestartCount < 3; second2++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var third = await WaitForAsync(supervisor, status => status.RestartCount == 3);
        Assert.Equal("运行单元发生未预期错误。", third.LastError);
        Assert.Equal(TimeSpan.FromMilliseconds(100), third.NextRestartAt - third.LastErrorAt);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task TheBackoffTimerIsArmedBeforeTheFailureIsPublished()
    {
        // 在记录故障事件的那一刻把时钟推进到退避到期：计时已经开始，因此按时重启。
        // 先公布、后计时的写法会错过这次推进，单元一直停在退避（按状态推进时钟的测试也会因此偶发失败）。
        var time = new FakeTimeProvider();
        var events = new RecordingEventSink(recorded =>
        {
            if (recorded.Type == RuntimeEventTypes.UnitFaulted)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
            }
        });
        var supervisor = Supervisor(time, events);
        var builds = 0;
        IRuntimeUnit Build() => Interlocked.Increment(ref builds) == 1
            ? new ScriptedUnit(UnitId, _ => throw new InvalidOperationException("模拟故障"))
            : new ScriptedUnit(UnitId, token => Task.Delay(System.Threading.Timeout.Infinite, token));

        supervisor.TryStart(Build(), Build, reads: null);

        await WaitForAsync(supervisor, status => status.RestartCount == 1 && status.State == RuntimeUnitState.Running && Volatile.Read(ref builds) == 2);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task AnExceptionInACancellationCallbackIsRecordedAndDoesNotEscape()
    {
        // 单元在取消回调中释放连接时出错：停止照常完成，错误记为停止故障，不向调用方抛出。
        var events = new RecordingEventSink();
        var supervisor = Supervisor(TimeProvider.System, events);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRuntimeUnit Build() => new ScriptedUnit(UnitId, async token =>
        {
            await using var release = token.Register(() => throw new IOException("释放连接失败"));
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        });

        supervisor.TryStart(Build(), Build, reads: null);
        await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Empty(await StopAllAsync(supervisor));
        await WaitForAsync(supervisor, status => status.State == RuntimeUnitState.Stopped && status.LastError is not null);
        Assert.Equal("停止时出错：运行单元输入输出失败，请检查连接与存储。", Status(supervisor).LastError);
        Assert.Equal(0, Status(supervisor).RestartCount);
        Assert.Single(events.OfType(RuntimeEventTypes.UnitStopFailed));
    }

    [Fact]
    public async Task ABlockingCancellationCallbackIsBoundedByTheStopTimeout()
    {
        // 取消回调（例如关闭连接）阻塞时，停止按期限返回并报告“停止超时”；回调结束、循环退出之后才变为已停止。
        var supervisor = Supervisor(TimeProvider.System, new RecordingEventSink());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRuntimeUnit Build() => new ScriptedUnit(UnitId, async token =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var release = token.Register(() =>
            {
                Thread.Sleep(1500);
                closed.TrySetResult();
            });
            started.TrySetResult();
            await closed.Task;
        });

        supervisor.TryStart(Build(), Build, reads: null);
        await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal([UnitId], timedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"停止用了 {stopwatch.Elapsed}。");
        Assert.Equal(RuntimeUnitState.StopTimedOut, Status(supervisor).State);
        Assert.True(supervisor.IsAlive(UnitId));
        await WaitForAsync(supervisor, status => status.State == RuntimeUnitState.Stopped);
        Assert.False(supervisor.IsAlive(UnitId));
    }

    [Fact]
    public async Task ACancellationCallbackStillRunningAfterTheLoopExitedKeepsTheIdentifierAndDelaysDisposal()
    {
        // 循环看到取消标志就自行退出，不等取消回调；回调（例如关闭连接）仍在阻塞：同样判定“停止超时”并占用标识，
        // 实例要等回调结束才释放（二者不同时操作同一连接），之后才变为已停止。
        var supervisor = Supervisor(TimeProvider.System, new RecordingEventSink());
        using var gate = new ManualResetEventSlim();
        var registration = default(CancellationTokenRegistration);
        var order = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loopExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRuntimeUnit Build() => new ScriptedUnit(
            UnitId,
            async token =>
            {
                registration = token.Register(() =>
                {
                    gate.Wait();
                    order.Enqueue("callback");
                });
                started.TrySetResult();
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }

                loopExited.TrySetResult();
            },
            () => order.Enqueue("dispose"));

        try
        {
            supervisor.TryStart(Build(), Build, reads: null);
            await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // 停止本身必须按期限返回：若等待回调，闸门不放行就永远不返回，这里在 10 秒后判失败，而不是挂起。
            var timedOut = await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)
                .WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await loopExited.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal([UnitId], timedOut);
            Assert.Equal(RuntimeUnitState.StopTimedOut, Status(supervisor).State);
            Assert.True(supervisor.IsAlive(UnitId));
            Assert.Empty(order);
        }
        finally
        {
            gate.Set();
        }

        await WaitForAsync(supervisor, status => status.State == RuntimeUnitState.Stopped);
        Assert.False(supervisor.IsAlive(UnitId));
        Assert.Equal(["callback", "dispose"], order);
        registration.Dispose();
    }

    [Fact]
    public async Task ALoopThatEndsWithoutAStopRequestIsAFailure()
    {
        var time = new FakeTimeProvider();
        var supervisor = Supervisor(time, new RecordingEventSink());
        IRuntimeUnit Build() => new ScriptedUnit(UnitId, _ => Task.CompletedTask);

        supervisor.TryStart(Build(), Build, reads: null);

        var status = await WaitForAsync(supervisor, unit => unit.State == RuntimeUnitState.Backoff);
        Assert.Equal("运行单元的循环在没有请求停止时结束。", status.LastError);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task InstancesNeverOverlapAndEachIsDisposedBeforeTheNextIsBuilt()
    {
        var time = new FakeTimeProvider();
        var supervisor = Supervisor(time, new RecordingEventSink());
        var log = new ConcurrentQueue<string>();
        var alive = 0;
        var maximumAlive = 0;
        var builds = 0;
        IRuntimeUnit Build()
        {
            var number = Interlocked.Increment(ref builds);
            log.Enqueue($"build {number}");
            return new ScriptedUnit(
                UnitId,
                async _ =>
                {
                    var now = Interlocked.Increment(ref alive);
                    InterlockedMax(ref maximumAlive, now);
                    log.Enqueue($"run {number}");
                    await Task.Yield();
                    Interlocked.Decrement(ref alive);
                    throw new InvalidOperationException("模拟故障");
                },
                () => log.Enqueue($"dispose {number}"));
        }

        supervisor.TryStart(Build(), Build, reads: null);
        for (var restart = 1; restart <= 5; restart++)
        {
            await WaitForAsync(supervisor, status => status.RestartCount == restart && status.State == RuntimeUnitState.Backoff);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        await WaitForAsync(supervisor, status => status.RestartCount == 6);
        Assert.Equal(1, maximumAlive);
        var expected = Enumerable.Range(1, 6).SelectMany(number => new[] { $"build {number}", $"run {number}", $"dispose {number}" });
        Assert.Equal(expected, log.Take(18));
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task AFailedRebuildIsRetriedAfterBackoff()
    {
        var time = new FakeTimeProvider();
        var supervisor = Supervisor(time, new RecordingEventSink());
        var builds = 0;
        IRuntimeUnit Build() => Interlocked.Increment(ref builds) switch
        {
            1 => new ScriptedUnit(UnitId, _ => throw new InvalidOperationException("模拟故障")),
            2 => throw new IOException("凭证暂时无法解密"),
            _ => new ScriptedUnit(UnitId, token => Task.Delay(System.Threading.Timeout.Infinite, token)),
        };

        supervisor.TryStart(Build(), Build, reads: null);
        await WaitForAsync(supervisor, status => status.RestartCount == 1);
        time.Advance(TimeSpan.FromMinutes(1));
        var rebuildFailed = await WaitForAsync(supervisor, status => status.RestartCount == 2);
        Assert.Equal("运行单元重新构建失败，请检查配置和凭证。", rebuildFailed.LastError);

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(supervisor, _ => builds == 3);
        await WaitForAsync(supervisor, status => status.State == RuntimeUnitState.Running);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task AUnitThatDoesNotStopKeepsItsIdentifierUntilItsLoopExits()
    {
        var events = new RecordingEventSink();
        var supervisor = Supervisor(TimeProvider.System, events);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 不响应取消的循环：停止期限到时仍在运行。
        IRuntimeUnit Stubborn() => new ScriptedUnit(UnitId, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        supervisor.TryStart(Stubborn(), Stubborn, reads: null);
        await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var timedOut = await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Equal([UnitId], timedOut);
        Assert.Equal(RuntimeUnitState.StopTimedOut, Status(supervisor).State);
        Assert.True(supervisor.IsAlive(UnitId));
        Assert.Single(events.OfType(RuntimeEventTypes.UnitStopTimedOut));

        // 再次停止：等待期间保持“停止超时”，不退回“停止中”；到期后仍报告超时，超时事件不重复记录。
        var again = supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeUnitState.StopTimedOut, Status(supervisor).State);
        Assert.Equal([UnitId], await again.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Single(events.OfType(RuntimeEventTypes.UnitStopTimedOut));

        // 单实例规则：上一个实例的循环没有退出，不启动同一标识的新实例。
        var replacementBuilt = false;
        IRuntimeUnit Replacement()
        {
            replacementBuilt = true;
            return new ScriptedUnit(UnitId, token => Task.Delay(System.Threading.Timeout.Infinite, token));
        }

        Assert.False(supervisor.TryStart(Replacement(), Replacement, reads: null));

        release.SetResult();
        await WaitForAsync(supervisor, status => status.State == RuntimeUnitState.Stopped);
        Assert.False(supervisor.IsAlive(UnitId));
        Assert.True(supervisor.TryStart(Replacement(), Replacement, reads: null));
        Assert.True(replacementBuilt);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    [Fact]
    public async Task AFaultyUnitDoesNotAffectTheOthers()
    {
        var time = new FakeTimeProvider();
        var supervisor = Supervisor(time, new RecordingEventSink());
        var healthyId = RuntimeUnitId.Create("input.test", "meter-2/line");
        var ticks = 0;
        IRuntimeUnit Faulty() => new ScriptedUnit(UnitId, _ => throw new InvalidOperationException("模拟故障"));
        IRuntimeUnit Healthy() => new ScriptedUnit(healthyId, async token =>
        {
            while (true)
            {
                Interlocked.Increment(ref ticks);
                await Task.Delay(TimeSpan.FromSeconds(1), time, token);
            }
        });

        supervisor.TryStart(Faulty(), Faulty, reads: null);
        supervisor.TryStart(Healthy(), Healthy, reads: null);
        for (var second = 0; second < 5; second++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await WaitForAsync(supervisor, _ => Volatile.Read(ref ticks) >= 5);
        var statuses = supervisor.Snapshot();
        Assert.Equal(RuntimeUnitState.Running, statuses.Single(status => status.Id == healthyId).State);
        Assert.True(statuses.Single(status => status.Id == UnitId).RestartCount >= 1);
        Assert.Empty(await StopAllAsync(supervisor));
    }

    private static UnitSupervisor Supervisor(TimeProvider time, RecordingEventSink events) => new(
        new RuntimeOptions { InitialRestartDelay = TimeSpan.FromMilliseconds(100), MaximumRestartDelay = TimeSpan.FromSeconds(10), RestartJitter = 0 },
        time,
        events,
        NullLogger.Instance,
        random: () => 0);

    /// <summary>停止全部单元（取消令牌会结束退避等待）；FakeTimeProvider 不会推进停止期限，因此用真实时间等待。</summary>
    private static Task<IReadOnlyList<RuntimeUnitId>> StopAllAsync(UnitSupervisor supervisor) =>
        supervisor.StopAsync(_ => true, Timeout, TestContext.Current.CancellationToken);

    private static RuntimeUnitStatus Status(UnitSupervisor supervisor) => supervisor.Snapshot().Single(status => status.Id == UnitId);

    private static async Task<RuntimeUnitStatus> WaitForAsync(UnitSupervisor supervisor, Func<RuntimeUnitStatus, bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = supervisor.Snapshot().FirstOrDefault(unit => unit.Id == UnitId);
            if (status is not null && condition(status))
            {
                return status;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"等待单元状态超时，当前为 {string.Join("；", supervisor.Snapshot())}。");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private sealed class ScriptedUnit(RuntimeUnitId id, Func<CancellationToken, Task> body, Action? onDispose = null) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;

        public Task RunAsync(CancellationToken stoppingToken) => body(stoppingToken);

        public ValueTask DisposeAsync()
        {
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}

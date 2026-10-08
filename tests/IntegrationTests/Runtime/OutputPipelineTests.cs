using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputPipelineTests
{
    [Fact]
    public async Task TimedOutFormatterKeepsItsBudgetUntilTheProcessIsRestarted()
    {
        using var data = TemporaryDirectory.Create();
        using var release = new ManualResetEventSlim();
        var formatting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new FakeOutputFactory
        {
            BeforeFormat = () =>
            {
                formatting.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "测试没有释放超时格式化器。");
            },
        };
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(100) });
        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        try
        {
            await formatting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var reserved = runtime.InFlight.Status.ReservedBytes;
            Assert.True(reserved > 0);
            await runtime.StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(reserved, runtime.InFlight.Status.ReservedBytes);
            Assert.Equal(RuntimeUnitState.StopTimedOut,
                Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "delivery.dispatcher").State);
            Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
            Assert.Equal(0, output.Sent);
        }
        finally { release.Set(); }

        await UntilAsync(() => runtime.GetStatus().Units.All(unit => unit.State == RuntimeUnitState.Stopped));
        // 停止超时的旧运行时不再激活；保守保留旧状态和预约，不能把它伪报成已安全回收。
        Assert.True(runtime.InFlight.Status.ReservedBytes > 0);
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task ThirtyTwoWayFanoutBackpressuresInputAtSharedBudgetAndDrainsAllAcceptedRecords()
    {
        const int sources = 250;
        const int paths = 32;
        using var data = TemporaryDirectory.Create();
        using var release = new ManualResetEventSlim();
        var input = new OneShotInputFactory(sources);
        var output = new FakeOutputFactory
        {
            BeforeFormat = () => Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "测试没有释放格式化闸门。"),
            OnSend = _ => new(OutputSendStatus.SuspendTarget, "test-paused"),
        };
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [output]);
        await using var plan = runtime.Prepare(Plan([Input(), .. Enumerable.Range(1, paths).Select(i => Output($"out-{i}", i))]));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        try
        {
            // 分发器卡在本地格式化，输入最多预约三条 32 路消息，第四条等待而不是继续分配。
            await UntilAsync(() => runtime.InFlight.Status.ReservedBytes >= 3L * paths * 2 * DeliveryDraft.MaximumPayloadBytes);
            Assert.False(input.Written.Task.IsCompleted);
            Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 1, runtime.InFlight.Status.CapacityBytes);
        }
        finally { release.Set(); }

        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sources * paths, runtime.Deliveries.GetStatus().CommittedRecords);
        for (var connection = 1; connection <= paths; connection++)
        {
            Assert.Equal(sources, runtime.Deliveries.ReadPending(connection, 0, sources + 1).Count);
        }

        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 1, runtime.InFlight.Status.CapacityBytes);
        Assert.All(runtime.GetStatus().Units, unit => Assert.Equal(RuntimeUnitState.Stopped, unit.State));
    }

    [Fact]
    public async Task FormatRejectionIsCountedByInputAndTargetAndDoesNotHoldBudget()
    {
        using var data = TemporaryDirectory.Create();
        var output = new FakeOutputFactory { RejectFormatting = true };
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, events, outputs: [output]);
        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => events.OfType("output.rejected").Count == 1 && runtime.InFlight.Status.ReservedBytes == 0);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["path/meter/sim/1/dataRejected"]);
        Assert.Equal(0, output.Sent);
        Assert.Equal(1, runtime.Deliveries.GetActivityTotals().RejectedPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamplingStorageFailureDropsTheMessageAndReleasesBudget(bool recover)
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new FakeOutputFactory { OnSend = _ => new(OutputSendStatus.SuspendTarget, "test") };
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, events, outputs: [output],
            options: new RuntimeOptions
            {
                UnitStopTimeout = TimeSpan.FromMilliseconds(200),
                InitialRestartDelay = TimeSpan.FromMilliseconds(10),
                RestartJitter = 0
            });
        _ = runtime.Deliveries.GetPersistentCounters();
        using (var connection = Open(data.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_budget_insert BEFORE INSERT ON delivery BEGIN SELECT * FROM test_budget_missing_table; END";
            command.ExecuteNonQuery();
        }

        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await UntilAsync(() => events.OfType("input.storageLoss").Count == 1 && runtime.InFlight.Status.ReservedBytes == 0);
        Assert.True(runtime.Deliveries.GetStatus().StorageFault);
        Assert.Equal(1, runtime.Deliveries.GetUnpersistedCounters()["storageLostSourceMessages"]);
        Assert.Equal(1, runtime.Deliveries.GetUnpersistedCounters()["storageLostDeliveryRecords"]);
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));

        if (recover)
        {
            using var connection = Open(data.Path);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_budget_insert";
            command.ExecuteNonQuery();
            var laterSource = Guid.NewGuid();
            var later = new DeliveryDraft(Guid.NewGuid(), 1, laterSource, "meter", 1, "{}"u8.ToArray(), "fingerprint", "test", "test", 1);
            Assert.True((await runtime.Deliveries.EnqueueAsync([later], TestContext.Current.CancellationToken)).Committed);
            Assert.Equal(laterSource, Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10)).SourceMessageId);
        }

        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.Equal(recover ? 1 : 0, runtime.Deliveries.GetStatus().CommittedRecords);
        Assert.Empty(events.OfType("dispatcher.drainTimedOut"));
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["storageLostSourceMessages"]);
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["storageLostDeliveryRecords"]);
    }

    [Fact]
    public async Task FanoutIsCommittedTogetherBeforeSendAndAckDeletesTheFrozenRecords()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new FakeOutputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [output]);
        output.OnSend = record =>
        {
            Assert.Equal(2, runtime.Deliveries.GetStatus().CommittedRecords);
            Assert.Equal(record.Payload.ToArray(), Assert.Single(runtime.Deliveries.ReadPending(record.Connection, 0, 10)).Payload.ToArray());
            return new(OutputSendStatus.Ack);
        };
        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1), Output("out-b", 2)));
        Assert.Empty(plan.Failures);
        Assert.Equal(0, input.Started);
        Assert.Equal(0, output.Sent);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 2);
        Assert.Equal(2, output.Sent);
        Assert.Equal(2, runtime.Deliveries.GetActivityTotals().Sent);
        Assert.Equal(2, runtime.Deliveries.GetStatus().CommittedRecords);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(output.Created, output.Disposed);
        Assert.All(runtime.GetStatus().Units, unit => Assert.Equal(RuntimeUnitState.Stopped, unit.State));
    }

    [Fact]
    public async Task MissingReferenceDuplicateConnectionAndClientIdAreStructuralFailures()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [new FakeOutputFactory()]);
        await using var missing = runtime.Prepare(Plan(Output("out-a", 1)));
        Assert.Single(missing.Failures);
        await using var duplicate = runtime.Prepare(Plan(Input(), Output("out-a", 1), Output("out-b", 1)));
        Assert.Single(duplicate.Failures);
        await using var conflictingClient = runtime.Prepare(Plan(Input(), Output("out-a", 1, "same"), Output("out-b", 2, "same")));
        Assert.Single(conflictingClient.Failures);
    }

    [Fact]
    public async Task ARejectedPreparedPlanNeverCreatesAdaptersOrBorrowsConnections()
    {
        using var data = TemporaryDirectory.Create();
        var output = new FakeOutputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [output]);
        await using (var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1), Output("out-b", 1))))
        {
            Assert.Single(plan.Failures);
            Assert.Equal(0, output.Created);
            Assert.Equal(0, output.Sent);
        }

        Assert.Equal(0, output.Disposed);
    }

    [Fact]
    public async Task AckedRecordIsNotRepublishedWhenDeletionFailsAndSenderIsRebuilt()
    {
        using var data = TemporaryDirectory.Create();
        var output = new FakeOutputFactory();
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, events, outputs: [output]);
        output.OnSend = _ =>
        {
            using var connection = Open(data.Path);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_delete BEFORE DELETE ON delivery BEGIN SELECT RAISE(ABORT, 'test-delete-failure'); END";
            command.ExecuteNonQuery();
            return new(OutputSendStatus.Ack);
        };
        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => events.OfType(RuntimeEventTypes.UnitFaulted).Any(entry =>
            entry.Target == Output("out-a", 1).Id.ToString()) && runtime.Deliveries.GetStatus().StorageFault);
        await UntilAsync(() => output.Created >= 2);
        Assert.True(runtime.Deliveries.GetStatus().StorageFault);
        Assert.Equal(0, runtime.Deliveries.GetStatus().RejectedRecords);
        Assert.Equal(1, output.Sent);
        Assert.Equal(1, runtime.Deliveries.GetActivityTotals().Sent);
        using (var connection = Open(data.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_delete";
            command.ExecuteNonQuery();
        }

        await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
        Assert.Equal(1, output.Sent);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task TargetFailureDoesNotBlockAnotherTargetAndStopPreservesUnacknowledgedRecords()
    {
        using var data = TemporaryDirectory.Create();
        var output = new FakeOutputFactory { OnSend = record => new(record.Connection == 1 ? OutputSendStatus.Retry : OutputSendStatus.Ack, "test-network-fault") };
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [output]);
        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1), Output("out-b", 2)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Empty(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Equal(output.Created, output.Disposed);
    }

    [Fact]
    public async Task ShutdownDrainsInputChannelBeforeFinalWriterCommit()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory(count: 10);
        var output = new FakeOutputFactory { OnSend = _ => new(OutputSendStatus.SuspendTarget, "test-suspended") };
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [output]);
        await using var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(10, runtime.Deliveries.GetStatus().CommittedRecords);
        Assert.Equal(10, runtime.Deliveries.ReadPending(1, 0, 20).Count);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    internal static RuntimePlan Plan(params RuntimeUnitPlan[] units) => new(1, units);
    internal static RuntimeUnitPlan Input() => new(RuntimeUnitId.Create("input.test", "meter/sim"), JsonSerializer.SerializeToElement(new { }));
    private static RuntimeUnitPlan Output(string key, long connection, string? clientId = null) => new(RuntimeUnitId.Create("output.test", key), JsonSerializer.SerializeToElement(new { connection, clientId }));
    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static SqliteConnection Open(string directory)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private sealed class FakeOutputFactory : IOutputAdapterFactory
    {
        private int _sent;
        private int _created;
        private int _disposed;
        public int Sent => Volatile.Read(ref _sent);
        public int Created => Volatile.Read(ref _created);
        public int Disposed => Volatile.Read(ref _disposed);
        public string Kind => "output.test";
        public bool RejectFormatting { get; init; }
        public Action? BeforeFormat { get; init; }
        public Func<SentRecord, OutputSendResult> OnSend { get; set; } = _ => new(OutputSendStatus.Ack);
        public IOutputTarget Build(RuntimeUnitPlan plan) => new Target(this, plan);
        private sealed class Target(FakeOutputFactory factory, RuntimeUnitPlan plan) : IOutputTarget
        {
            public RuntimeUnitId Id => plan.Id;
            public long ConnectionId => plan.Configuration.GetProperty("connection").GetInt64();
            public string ClientId => plan.Configuration.GetProperty("clientId").GetString() ?? "client-" + ConnectionId;
            public IReadOnlyList<string> Inputs => ["meter/sim"];
            public string Recipient => "mqtt://127.0.0.1:1883";
            public string CurrentFingerprint => "fingerprint";
            public string CredentialVersion => "test-version";
            public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
            {
                factory.BeforeFormat?.Invoke();
                return factory.RejectFormatting
                    ? throw new ArgumentException("test formatting rejection")
                    : new(StandardEnvelope.Serialize(message, deliveryId), "test", 1, "fingerprint", "test");
            }
            public IOutputAdapter CreateAdapter()
            {
                Interlocked.Increment(ref factory._created);
                return new Adapter(factory, ConnectionId);
            }
        }

        private sealed class Adapter(FakeOutputFactory factory, long connection) : IOutputAdapter
        {
            public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref factory._sent);
                return Task.FromResult(factory.OnSend(new SentRecord(connection, message.Payload)));
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref factory._disposed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record SentRecord(long Connection, ReadOnlyMemory<byte> Payload);
}

internal sealed class OneShotInputFactory(int count = 1) : IInputUnitFactory
{
    private int _started;
    public int Started => Volatile.Read(ref _started);
    public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Kind => "input.test";
    public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) => new Unit(this, plan.Id, context, count);
    private sealed class Unit(OneShotInputFactory owner, RuntimeUnitId id, InputUnitContext context, int count) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref owner._started);
            for (var i = 0; i < count; i++)
            {
                await context.Messages.ProduceAsync(1024, _ =>
                {
                    var at = context.Time.GetUtcNow();
                    return ValueTask.FromResult<EdgeMessage?>(new EdgeMessage(Guid.NewGuid(), context.InputKey, context.DeviceKey,
                        at, TimeSource.GatewayUnsynced, at, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42 + i) }));
                }, stoppingToken);
            }

            owner.Written.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

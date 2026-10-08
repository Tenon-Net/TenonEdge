using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputPipelineTests
{
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
    public async Task ARejectedPreparedPlanDisposesAllConstructedSendersWithoutNetwork()
    {
        using var data = TemporaryDirectory.Create();
        var output = new FakeOutputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [output]);
        await using (var plan = runtime.Prepare(Plan(Input(), Output("out-a", 1), Output("out-b", 1))))
        {
            Assert.Single(plan.Failures);
            Assert.Equal(1, output.Created);
            Assert.Equal(0, output.Sent);
        }

        Assert.Equal(1, output.Disposed);
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
        await UntilAsync(() => events.Events.Any(entry => entry.Description.Contains("删除未提交", StringComparison.Ordinal)));
        await UntilAsync(() => output.Created >= 2);
        Assert.True(runtime.Deliveries.GetStatus().StorageFault);
        Assert.Equal(0, runtime.Deliveries.GetStatus().RejectedRecords);
        Assert.Equal(1, output.Sent);
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
        public Func<SentRecord, OutputSendResult> OnSend { get; set; } = _ => new(OutputSendStatus.Ack);
        public IOutputTarget Build(RuntimeUnitPlan plan) => new Target(this, plan);
        private sealed class Target(FakeOutputFactory factory, RuntimeUnitPlan plan) : IOutputTarget
        {
            public RuntimeUnitId Id => plan.Id;
            public long ConnectionId => plan.Configuration.GetProperty("connection").GetInt64();
            public string ClientId => plan.Configuration.GetProperty("clientId").GetString() ?? "client-" + ConnectionId;
            public IReadOnlyList<string> Inputs => ["meter/sim"];
            public string Recipient => "mqtt://127.0.0.1:1883";
            public string CredentialVersion => "test-version";
            public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => new(StandardEnvelope.Serialize(message, deliveryId), "test", 1, "fingerprint", "test");
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
                var at = context.Time.GetUtcNow();
                await context.Messages.WriteAsync(new EdgeMessage(Guid.NewGuid(), context.InputKey, context.DeviceKey, at, TimeSource.GatewayUnsynced, at,
                    new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42 + i) }), stoppingToken);
            }

            owner.Written.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

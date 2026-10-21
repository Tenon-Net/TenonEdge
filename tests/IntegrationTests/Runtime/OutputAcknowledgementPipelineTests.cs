using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputAcknowledgementPipelineTests
{
    [Fact]
    public async Task ConfirmedRecordsAdvanceInFifoOrderBeforeDeletionCommits()
    {
        await using var h = await Harness.CreateAsync(3);
        var expected = Enumerable.Range(1, 3).ToArray();
        using var connection = h.Open();
        SqliteTransaction? held = null;
        h.Target.OnSend = value =>
        {
            if (value == 1)
            { held = connection.BeginTransaction(deferred: false); }
            return new(OutputSendStatus.Ack);
        };
        try
        {
            h.Start();
            await UntilAsync(() => h.Target.Calls.Count == 3);
            Assert.Equal(expected, h.Target.Calls);
            Assert.Equal(3, h.Cache.GetQueueTotals(1).PendingCount);
            Assert.Equal(0, h.Cache.GetStatus().DeletedRecords);
        }
        finally { held?.Dispose(); }
        await UntilAsync(() => h.Cache.GetStatus().DeletedRecords == 3);
        Assert.Equal(expected, h.Target.Calls);
    }

    [Fact]
    public async Task StalledStorageBoundsAcknowledgementsAndDoesNotRetainPayloadBatches()
    {
        const int count = OutputSenderState.MaximumPendingAcknowledgements + 10;
        await using var h = await Harness.CreateAsync(count);
        using var connection = h.Open();
        SqliteTransaction? held = null;
        h.Target.OnSend = value =>
        {
            if (value == 1)
            { held = connection.BeginTransaction(deferred: false); }
            return new(OutputSendStatus.Ack);
        };
        try
        {
            h.Start();
            await UntilAsync(() => h.State.PendingAcknowledgementCount == OutputSenderState.MaximumPendingAcknowledgements);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(OutputSenderState.MaximumPendingAcknowledgements, h.Target.Calls.Count);
            Assert.Equal(count, h.Cache.GetQueueTotals(1).PendingCount);
            Assert.Equal(0, h.Cache.GetStatus().DeletedRecords);
        }
        finally { held?.Dispose(); }
        await UntilAsync(() => h.Cache.GetStatus().DeletedRecords == count && h.State.PendingAcknowledgementCount == 0);
        Assert.Equal(Enumerable.Range(1, count), h.Target.Calls);
        Assert.Equal(0, h.State.PendingAcknowledgementCount);
    }

    [Fact]
    public async Task QueuePauseWaitsForEveryConfirmationBeforeManualClear()
    {
        await using var h = await Harness.CreateAsync(20);
        using var connection = h.Open();
        SqliteTransaction? held = null;
        OutputSenderState.QueuePause? pause = null;
        h.Target.OnSend = value =>
        {
            if (value == 1)
            { held = connection.BeginTransaction(deferred: false); }
            if (value == 3)
            { pause = h.State.PauseQueue(); }
            return new(OutputSendStatus.Ack);
        };
        try
        {
            h.Start();
            await UntilAsync(() => h.State.PendingAcknowledgementCount == 3);
            Assert.NotNull(pause);
            Assert.False(pause.Reached.Task.IsCompleted);
            held?.Dispose();
            held = null;
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(0, h.State.PendingAcknowledgementCount);
            Assert.Equal(3, h.Cache.GetStatus().DeletedRecords);
            Assert.Equal(3, h.Target.Calls.Count);
            Assert.Equal(17, (await h.Cache.ClearPendingAsync(1)).AffectedRecords);
            Assert.Equal(17, h.Cache.GetPersistentCounters()["manuallyClearedPendingRecords"]);
        }
        finally
        {
            held?.Dispose();
            pause?.Dispose();
        }
        Assert.Equal(0, h.Cache.GetQueueTotals(1).PendingCount);
    }

    [Fact]
    public async Task FailedDeletionRetainsAllConfirmationsAcrossSenderReplacement()
    {
        await using var h = await Harness.CreateAsync(20);
        h.Sql("CREATE TRIGGER fail_ack_delete BEFORE DELETE ON delivery BEGIN SELECT RAISE(ABORT, 'test delete failure'); END;");
        h.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.SenderTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(20, h.Cache.GetQueueTotals(1).PendingCount);
        Assert.InRange(h.Target.Calls.Count, 1, 20);
        Assert.True(h.Cache.GetStatus().StorageFault);
        h.Sql("DROP TRIGGER fail_ack_delete");
        await h.ReplaceAsync();
        await UntilAsync(() => h.Cache.GetStatus().DeletedRecords == 20);
        Assert.Equal(Enumerable.Range(1, 20), h.Target.Calls);
        Assert.Equal(20, h.Cache.GetPersistentCounters()["acknowledgedRecords"]);
    }

    [Fact]
    public async Task PartialRejectionDoesNotAcknowledgeOtherRecordsOrChangeFifo()
    {
        await using var h = await Harness.CreateAsync(6);
        h.Target.OnSend = value => new(value == 3 ? OutputSendStatus.RejectMessage : OutputSendStatus.Ack);
        h.Start();
        await UntilAsync(() => h.Cache.GetStatus().DeletedRecords == 5);
        Assert.Equal(3, BitConverter.ToInt32(Assert.Single(h.Cache.ReadFailed(1, 0, 10)).Payload.Span));
        Assert.Empty(h.Cache.ReadPending(1, 0, 10));
        Assert.Equal(Enumerable.Range(1, 6), h.Target.Calls);
        Assert.Equal(5, h.Cache.GetPersistentCounters()["acknowledgedRecords"]);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        while (!condition())
        { await Task.Delay(5, deadline.Token); }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly RuntimeHost _runtime;
        private OutputSenderUnit? _sender;
        internal Task? SenderTask { get; private set; }
        internal OutputSenderState State { get; } = new();
        internal Target Target { get; } = new();
        internal DeliveryCache Cache => _runtime.Deliveries;

        private Harness()
        {
            _runtime = RuntimeTesting.CreateRuntime([], _directory.Path);
            _runtime.StartCore();
        }

        internal static async Task<Harness> CreateAsync(int count)
        {
            var h = new Harness();
            var drafts = Enumerable.Range(1, count).Select(value => new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1,
                BitConverter.GetBytes(value), "fingerprint", "summary", targetAddress: "frozen-topic", qualityOfService: 1)).ToArray();
            Assert.True((await h.Cache.EnqueueAsync(drafts, TestContext.Current.CancellationToken)).Committed);
            return h;
        }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(_directory.Path, DeliveryCacheOptions.FileName)};Pooling=False");
            connection.Open();
            return connection;
        }

        internal void Start()
        {
            _sender = new OutputSenderUnit(Target, State, Cache, TimeProvider.System, new RecordingEventSink(),
                new RestartBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), 0, () => 0));
            SenderTask = _sender.RunAsync(_stop.Token);
        }

        internal async Task ReplaceAsync()
        {
            if (_sender is not null)
            { await _sender.DisposeAsync(); }
            Start();
        }

        internal void Sql(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (SenderTask is not null)
            {
                try
                { await SenderTask; }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            if (_sender is not null)
            { await _sender.DisposeAsync(); }
            await _runtime.DisposeAsync();
            _stop.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class Target : IOutputTarget
    {
        internal ConcurrentQueue<int> Calls { get; } = new();
        internal Func<int, OutputSendResult> OnSend { get; set; } = _ => new(OutputSendStatus.Ack);
        public RuntimeUnitId Id => RuntimeUnitId.Create("output.test", "ack-pipeline");
        public long ConnectionId => 1;
        public string? ClientId => null;
        public string Recipient => "test";
        public string CredentialVersion => "initial";
        public string CurrentFingerprint => "fingerprint";
        public IReadOnlyList<string> Inputs => [];
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => throw new NotSupportedException();
        public IOutputAdapter CreateAdapter() => new Adapter(this);

        private sealed class Adapter(Target target) : IOutputAdapter
        {
            public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                var value = BitConverter.ToInt32(message.Payload.Span);
                target.Calls.Enqueue(value);
                return Task.FromResult(target.OnSend(value));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

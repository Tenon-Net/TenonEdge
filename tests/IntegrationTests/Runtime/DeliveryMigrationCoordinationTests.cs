using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryMigrationCoordinationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(OutputSendStatus.Ack, 2)]
    [InlineData(OutputSendStatus.RejectMessage, 3)]
    [InlineData(OutputSendStatus.Retry, 3)]
    [InlineData(OutputSendStatus.SuspendTarget, 3)]
    public async Task BothOriginalSendersCommitOutcomesBeforeMigrationThenTargetResumesFrozenFifo(OutputSendStatus sourceOutcome, int moved)
    {
        using var data = TemporaryDirectory.Create();
        var source = new Target(1);
        var target = new Target(2);
        await using var runtime = Create(data.Path, source, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "a"), Draft(1, "b"), Draft(1, "c"), Draft(2, "target-a"), Draft(2, "target-b")], Token);
        var originals = runtime.Deliveries.ReadPending(1, 0, 10);
        var command = Command(originals);
        await Activate(runtime, source, target);
        try
        {
            await Task.WhenAll(source.Sending.Task, target.Sending.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            var moving = runtime.MigrateDeliveriesAsync(command, 1, Token);
            Assert.False(moving.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ClearPendingAsync(1, 1, Token));
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            Assert.False(moving.IsCompleted); // Target is ready; original source still owns its in-flight request.
            Assert.Null(runtime.Deliveries.ReadMigrationReceipt(command.OperationId));
            source.Result.TrySetResult(new(sourceOutcome, "test outcome", RetryAfter: TimeSpan.FromSeconds(60)));
            var result = await moving.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(result.Committed, result.Error);
            Assert.Equal(moved, result.AffectedRecords);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Empty(runtime.Deliveries.ReadFailed(1, 0, 10));
            await OutputPipelineTests.UntilAsync(() => target.Messages.Count == 2);
            var next = target.Messages.ToArray()[1];
            Assert.Equal(originals[sourceOutcome is OutputSendStatus.Ack or OutputSendStatus.RejectMessage ? 1 : 0].Payload.ToArray(), next.Payload.ToArray());
            Assert.Equal("frozen/path", next.Address);
            Assert.Equal("fp2", next.Fingerprint);
            Assert.Equal(sourceOutcome == OutputSendStatus.RejectMessage ? 1 : 0, runtime.Deliveries.ReadFailed(2, 0, 10).Count);
            var migrated = runtime.Deliveries.ReadPending(2, 0, 10).Concat(runtime.Deliveries.ReadFailed(2, 0, 10)).Where(record => record.MigrationId is not null).ToArray();
            Assert.Equal(moved, migrated.Length);
            Assert.All(migrated, record => Assert.Equal(0, record.FailureCount));
            Assert.Equal(sourceOutcome == OutputSendStatus.Ack ? 2 : 1, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Single(source.Messages);
            Assert.Equal(1, source.Creates);
            Assert.Equal(1, target.Creates);
            Assert.Equal(0, source.Disposes + target.Disposes);
            if (sourceOutcome == OutputSendStatus.SuspendTarget)
            { Assert.Equal("automaticallyPaused", runtime.GetOutputSenders().Single(item => item.ConnectionId == 1).Sender.State); }
        }
        finally { source.Release(); target.Release(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EitherSenderTimeoutRefusesWithoutMovingOrReplacingAndLateAckStillCommits(long blocked)
    {
        using var data = TemporaryDirectory.Create();
        var source = new Target(1) { SendTimeout = TimeSpan.FromMilliseconds(120) };
        var target = new Target(2) { SendTimeout = TimeSpan.FromMilliseconds(120) };
        await using var runtime = Create(data.Path, source, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "a"), Draft(2, "b")], Token);
        var command = Command(runtime.Deliveries.ReadPending(1, 0, 10));
        await Activate(runtime, source, target);
        try
        {
            await Task.WhenAll(source.Sending.Task, target.Sending.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            (blocked == 1 ? target : source).Result.TrySetResult(new(OutputSendStatus.SuspendTarget));
            var result = await runtime.MigrateDeliveriesAsync(command, 1, Token).WaitAsync(TimeSpan.FromSeconds(3), Token);
            Assert.Equal(DeliveryMigrationErrors.NotQuiescent, result.Error);
            Assert.Null(runtime.Deliveries.ReadMigrationReceipt(command.OperationId));
            Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
            (blocked == 1 ? source : target).Result.TrySetResult(new(OutputSendStatus.Ack));
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            Assert.Equal(1, source.Creates);
            Assert.Equal(1, target.Creates);
            Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        }
        finally { source.Release(); target.Release(); }
    }

    [Fact]
    public async Task CallerCancellationCannotReleaseEitherPauseOrEndOwnedOperationAndStopWaits()
    {
        using var data = TemporaryDirectory.Create();
        var source = new Target(1);
        var target = new Target(2);
        await using var runtime = Create(data.Path, source, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "a"), Draft(1, "b"), Draft(2, "c")], Token);
        var command = Command(runtime.Deliveries.ReadPending(1, 0, 10));
        await Activate(runtime, source, target);
        try
        {
            await Task.WhenAll(source.Sending.Task, target.Sending.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var moving = runtime.MigrateDeliveriesAsync(command, 1, caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
            var stop = runtime.StopAsync(Token);
            Assert.False(stop.IsCompleted);
            source.Result.TrySetResult(new(OutputSendStatus.Ack));
            target.Result.TrySetResult(new(OutputSendStatus.SuspendTarget));
            await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(1, runtime.Deliveries.ReadMigrationReceipt(command.OperationId)!.AffectedRecords);
            Assert.Equal(1, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Single(source.Messages);
            Assert.Equal(1, source.Creates);
            Assert.Equal(1, target.Creates);
        }
        finally { source.Release(); target.Release(); }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("protocol")]
    [InlineData("fingerprintBefore")]
    [InlineData("fingerprintDuring")]
    [InlineData("quiesced")]
    public async Task RevisionProtocolAndIdentityChangesCannotMoveRecords(string invalid)
    {
        using var data = TemporaryDirectory.Create();
        var source = new Target(1, invalid == "protocol" ? "output.mqtt" : "output.http");
        var target = new Target(2);
        await using var runtime = Create(data.Path, source, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "a"), Draft(2, "b")], Token);
        var command = Command(runtime.Deliveries.ReadPending(1, 0, 10));
        await Activate(runtime, source, target);
        try
        {
            await Task.WhenAll(source.Sending.Task, target.Sending.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (invalid == "fingerprintBefore")
            { target.Fingerprint = "changed"; }
            if (invalid == "quiesced")
            { await runtime.QuiesceInputsAsync(Token); }
            var moving = runtime.MigrateDeliveriesAsync(command, invalid == "revision" ? 2 : 1, Token);
            if (invalid == "fingerprintDuring")
            { target.Fingerprint = "changed"; }
            source.Release();
            target.Release();
            var result = await moving;
            Assert.Equal(invalid switch
            {
                "protocol" => DeliveryMigrationErrors.ProtocolMismatch,
                "fingerprintBefore" or "fingerprintDuring" => DeliveryMigrationErrors.TargetChanged,
                _ => DeliveryMigrationErrors.Unavailable,
            }, result.Error);
            Assert.Null(runtime.Deliveries.ReadMigrationReceipt(command.OperationId));
            Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        }
        finally { source.Release(); target.Release(); }
    }

    [Fact]
    public async Task SameConnectionUsesOnePauseAndMissingOriginalConnectionDoesNotInventASender()
    {
        using var data = TemporaryDirectory.Create();
        var target = new Target(2);
        target.Release();
        await using var runtime = Create(data.Path, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "orphan"), Draft(2, "same")], Token);
        var selected = runtime.Deliveries.ReadPending(1, 0, 10);
        await Activate(runtime, target);
        Assert.True((await runtime.MigrateDeliveriesAsync(Command(selected), 1, Token)).Committed);
        var same = DeliveryMigrationCommand.OldTarget(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 2, "fp2", "fp2", "safe2");
        Assert.True((await runtime.MigrateDeliveriesAsync(same, 1, Token)).Committed);
        Assert.Equal(1, target.Creates);
        Assert.Single(runtime.GetOutputSenders());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UncommittedAckBlocksMigrationAcrossSupervisorRebuildWithoutResending(long failingTarget)
    {
        using var data = TemporaryDirectory.Create();
        var source = new Target(1) { SendTimeout = TimeSpan.FromMilliseconds(500) };
        var target = new Target(2) { SendTimeout = TimeSpan.FromMilliseconds(500) };
        await using var runtime = Create(data.Path, source, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "a"), Draft(2, "b")], Token);
        var command = Command(runtime.Deliveries.ReadPending(1, 0, 10));
        await Activate(runtime, source, target);
        try
        {
            await Task.WhenAll(source.Sending.Task, target.Sending.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Sql(data.Path, $"CREATE TRIGGER fail_ack BEFORE DELETE ON delivery WHEN OLD.connection_id={failingTarget} BEGIN SELECT abs(-9223372036854775808); END;");
            var blocked = failingTarget == 1 ? source : target;
            blocked.Result.TrySetResult(new(OutputSendStatus.Ack));
            (failingTarget == 1 ? target : source).Release();
            await OutputPipelineTests.UntilAsync(() => blocked.Creates > 1);
            var result = await runtime.MigrateDeliveriesAsync(command, 1, Token);
            Assert.Equal(DeliveryMigrationErrors.NotQuiescent, result.Error);
            Assert.Null(runtime.Deliveries.ReadMigrationReceipt(command.OperationId));
            Assert.Single(blocked.Messages);
            Sql(data.Path, "DROP TRIGGER fail_ack");
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            var retried = await runtime.MigrateDeliveriesAsync(command, 1, Token);
            Assert.True(retried.Committed, retried.Error);
            Assert.Equal(failingTarget == 1 ? 0 : 1, retried.AffectedRecords);
            // The target may now send the newly migrated source record; only the ACKed original must never repeat.
            var originalBody = failingTarget == 1 ? "a" : "b";
            Assert.Single(blocked.Messages, message => System.Text.Encoding.UTF8.GetString(message.Payload.Span) == originalBody);
        }
        finally { source.Release(); target.Release(); Sql(data.Path, "DROP TRIGGER IF EXISTS fail_ack"); }
    }

    [Fact]
    public async Task FailedQueueTransactionReleasesBothPausesButKeepsAutomaticSuspension()
    {
        using var data = TemporaryDirectory.Create();
        var source = new Target(1);
        var target = new Target(2);
        source.Release();
        target.Release();
        await using var runtime = Create(data.Path, source, target);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1, "a"), Draft(2, "b")], Token);
        var command = Command(runtime.Deliveries.ReadPending(1, 0, 10));
        await Activate(runtime, source, target);
        await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().All(item => item.Sender.State == "automaticallyPaused"));
        Sql(data.Path, "CREATE TRIGGER fail_migration BEFORE INSERT ON queue_counters WHEN NEW.name='manuallyMigratedRecords' BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await runtime.MigrateDeliveriesAsync(command, 1, Token)).Status);
        Assert.Null(runtime.Deliveries.ReadMigrationReceipt(command.OperationId));
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Sql(data.Path, "DROP TRIGGER fail_migration");
        Assert.True((await runtime.MigrateDeliveriesAsync(command, 1, Token)).Committed);
        Assert.All(runtime.GetOutputSenders(), item => Assert.Equal("automaticallyPaused", item.Sender.State));
        Assert.Single(source.Messages);
        Assert.Single(target.Messages);
        Assert.Equal(1, source.Creates);
        Assert.Equal(1, target.Creates);
    }

    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(path, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static RuntimeHost Create(string path, params Target[] targets) => RuntimeTesting.CreateRuntime([], path,
        outputs: targets.Select(target => target.Id.Kind).Distinct().Select(kind => new Factory(kind, targets)));
    private static async Task Activate(RuntimeHost runtime, params Target[] targets)
    {
        await using var prepared = runtime.Prepare(new(1, targets.Select(target => new RuntimeUnitPlan(target.Id,
            JsonSerializer.SerializeToElement(new { connectionId = target.ConnectionId }))).ToArray()));
        Assert.Empty(prepared.Failures);
        await runtime.ActivateAsync(prepared);
    }
    private static DeliveryDraft Draft(long target, string body) => new(Guid.NewGuid(), target, Guid.NewGuid(), "meter", 1,
        System.Text.Encoding.UTF8.GetBytes(body), $"fp{target}", $"safe{target}", "frozen/path");
    private static DeliveryMigrationCommand Command(IReadOnlyList<StoredDelivery> records) => DeliveryMigrationCommand.Selected(
        RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, 2, "fp2", "safe2",
        records.Select(record => new DeliveryMigrationItem(record.Seq, record.DeliveryId, record.Fingerprint, record.MigrationId)).ToArray());
    private sealed class Factory(string kind, Target[] targets) : IOutputAdapterFactory
    {
        public string Kind => kind;
        public IOutputTarget Build(RuntimeUnitPlan plan) => targets.Single(target => target.Id == plan.Id);
    }
    private sealed class Target(long connection, string kind = "output.http") : IOutputTarget
    {
        public RuntimeUnitId Id => RuntimeUnitId.Create(kind, $"target{connection}");
        public long ConnectionId => connection;
        public IReadOnlyList<string> Inputs => [];
        public string? ClientId => null;
        public string Recipient => $"safe{connection}";
        public string CredentialVersion => "v1";
        public string Fingerprint = $"fp{connection}";
        public string CurrentFingerprint => Volatile.Read(ref Fingerprint);
        public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public int Creates;
        public int Disposes;
        public ConcurrentQueue<OutputMessage> Messages { get; } = new();
        public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OutputSendResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Result.TrySetResult(new(OutputSendStatus.SuspendTarget));
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => throw new NotSupportedException();
        public IOutputAdapter CreateAdapter() { Interlocked.Increment(ref Creates); return new Adapter(this); }
        private sealed class Adapter(Target owner) : IOutputAdapter
        {
            public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                owner.Messages.Enqueue(message);
                owner.Sending.TrySetResult();
                return owner.Messages.Count == 1 ? await owner.Result.Task : new(OutputSendStatus.SuspendTarget);
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposes); return ValueTask.CompletedTask; }
        }
    }
}

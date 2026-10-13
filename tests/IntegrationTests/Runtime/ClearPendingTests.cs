using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ClearPendingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(OutputSendStatus.Ack, 1)]
    [InlineData(OutputSendStatus.RejectMessage, 1)]
    [InlineData(OutputSendStatus.Retry, 2)]
    [InlineData(OutputSendStatus.SuspendTarget, 2)]
    public async Task WaitsForOriginalInFlightOutcomeBeforeAtomicClearAndPreservesOtherAreas(OutputSendStatus outcome, int cleared)
    {
        using var data = TemporaryDirectory.Create();
        var output = new ControlledOutput();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output]);
        runtime.StartCore();
        var drafts = new[] { Draft(1), Draft(1), Draft(1), Draft(1) };
        var seqs = (await runtime.Deliveries.EnqueueAsync(drafts, Token)).Seqs;
        await runtime.Deliveries.RecordFailureAsync(new(seqs[2], 1, "existing failed", true, false), Token);
        await runtime.Deliveries.QuarantineAsync(seqs[3], DeliveryQuarantineReasons.TargetChanged, Token);
        await Activate(runtime);
        await runtime.Deliveries.EnqueueAsync([Draft(2)], Token);
        try
        {
            await output.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var clear = runtime.ClearPendingAsync(1, 1, Token);
            Assert.False(clear.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.QuiesceInputsAsync(Token));
            Assert.Equal(2, runtime.Deliveries.ReadPending(1, 0, 10).Count);
            output.Result.TrySetResult(new(outcome, "safe-test-error", RetryAfter: TimeSpan.FromSeconds(60)));
            var result = await clear.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(result.Committed);
            Assert.Equal(cleared, result.AffectedRecords);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
            Assert.Equal(outcome == OutputSendStatus.RejectMessage ? 2 : 1, runtime.Deliveries.ReadFailed(1, 0, 10).Count);
            Assert.Single(runtime.Deliveries.ReadQuarantined(1, 0, 10));
            Assert.Equal(cleared, runtime.Deliveries.GetPersistentCounters()["manuallyClearedPendingRecords"]);
            Assert.Equal(cleared, runtime.Deliveries.GetPersistentCounters()["target/1/manuallyClearedPendingRecords"]);
            Assert.Equal(outcome == OutputSendStatus.Ack ? 1 : 0, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(1, output.Sends);
            Assert.Equal(1, output.Creates);
            Assert.Equal(0, output.Disposes);
            // New records after the DELETE boundary are retained; clear never freezes or stops producers.
            await runtime.Deliveries.EnqueueAsync([Draft(1)], Token);
            if (outcome == OutputSendStatus.Ack || outcome == OutputSendStatus.RejectMessage)
            { await OutputPipelineTests.UntilAsync(() => output.Sends == 2); }
            else
            { Assert.Equal(1, output.Sends); }
            Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            if (outcome == OutputSendStatus.SuspendTarget)
            { Assert.Equal("automaticallyPaused", Assert.Single(runtime.GetOutputSenders()).Sender.State); }
            if (outcome == OutputSendStatus.Retry)
            { await OutputPipelineTests.UntilAsync(() => Assert.Single(runtime.GetOutputSenders()).Sender.BackoffVersion is not null); }
        }
        finally { output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget)); }
    }

    [Fact]
    public async Task TimeoutDoesNotDeleteOrReplaceSenderAndLateAckIsCommittedNormally()
    {
        using var data = TemporaryDirectory.Create();
        var output = new ControlledOutput();
        // 第一条发送由闸门一直阻塞，仍必定超时；后续正常暂停检查须覆盖原循环250ms轮询周期。
        // 80ms会让第二次合法清空也随机超时，不能把轮询尚未醒来误判为未完成原ACK。
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromSeconds(2) });
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1), Draft(1)], Token);
        await Activate(runtime);
        try
        {
            await output.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var result = await runtime.ClearPendingAsync(1, 1, Token);
            Assert.Equal("delivery.clearPendingNotQuiescent", result.Error);
            Assert.Equal(2, runtime.Deliveries.ReadPending(1, 0, 10).Count);
            Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("manuallyClearedPendingRecords"));
            Assert.Equal(1, output.Sends);
            Assert.Equal(1, output.Creates);
            output.Result.TrySetResult(new(OutputSendStatus.Ack));
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1 && output.Sends == 2);
            await OutputPipelineTests.UntilAsync(() => Assert.Single(runtime.GetOutputSenders()).Sender.State == "automaticallyPaused");
            var cleared = await runtime.ClearPendingAsync(1, 1, Token);
            Assert.True(cleared.Committed, cleared.Error);
            Assert.Equal(1, cleared.AffectedRecords);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(1, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["manuallyClearedPendingRecords"]);
            Assert.Equal(1, output.Creates);
        }
        finally { output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget)); }
    }

    [Fact]
    public async Task UncommittedAckPreventsClearEvenAcrossSupervisorRestartAndIsNeverResent()
    {
        using var data = TemporaryDirectory.Create();
        var output = new ControlledOutput();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(250), InitialRestartDelay = TimeSpan.FromMilliseconds(10), RestartJitter = 0 });
        runtime.StartCore();
        var first = Assert.Single((await runtime.Deliveries.EnqueueAsync([Draft(1)], Token)).Seqs);
        await Activate(runtime);
        try
        {
            await output.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Sql(data.Path, $"CREATE TRIGGER fail_ack BEFORE DELETE ON delivery WHEN OLD.seq = {first} BEGIN SELECT abs(-9223372036854775808); END;");
            output.Result.TrySetResult(new(OutputSendStatus.Ack));
            var clear = await runtime.ClearPendingAsync(1, 1, Token);
            Assert.Equal("delivery.clearPendingNotQuiescent", clear.Error);
            Assert.True(output.Creates > 1);
            Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(1, output.Sends);
            Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("manuallyClearedPendingRecords"));
            Sql(data.Path, "DROP TRIGGER fail_ack;");
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            Assert.True((await runtime.ClearPendingAsync(1, 1, Token)).Committed);
            Assert.Equal(1, output.Sends);
        }
        finally { output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget)); Sql(data.Path, "DROP TRIGGER IF EXISTS fail_ack;"); }
    }

    [Fact]
    public async Task CallerCancellationDoesNotReleasePauseOrLifecycleAdmissionBeforeOwnedClearCompletes()
    {
        using var data = TemporaryDirectory.Create();
        var output = new ControlledOutput();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output]);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1), Draft(1)], Token);
        await Activate(runtime);
        try
        {
            await output.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var clear = runtime.ClearPendingAsync(1, 1, caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear);
            var stop = runtime.StopAsync(Token);
            Assert.False(stop.IsCompleted);
            output.Result.TrySetResult(new(OutputSendStatus.Ack));
            await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["manuallyClearedPendingRecords"]);
            Assert.Equal(1, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(1, output.Sends);
        }
        finally { output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget)); }
    }

    [Fact]
    public async Task MissingTargetStaleRevisionAndInvalidInputsCannotClear()
    {
        using var data = TemporaryDirectory.Create();
        var output = new ControlledOutput();
        output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget));
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output]);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1)], Token);
        await Activate(runtime);
        await runtime.Deliveries.EnqueueAsync([Draft(2)], Token);
        Assert.Equal("delivery.clearPendingUnavailable", (await runtime.ClearPendingAsync(1, 2, Token)).Error);
        Assert.Equal("delivery.clearPendingUnavailable", (await runtime.ClearPendingAsync(2, 1, Token)).Error);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ClearPendingAsync(0, 1, Token));
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
    }

    [Fact]
    public async Task WriterClearsMoreThanThousandAtomicallyAndPersistsOnlyManualCountersWithRollbackOnFault()
    {
        using var data = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync(Enumerable.Range(0, 1025).Select(_ => Draft(1)).ToArray(), Token);
            Sql(data.Path, "CREATE TRIGGER fail_clear BEFORE INSERT ON queue_counters WHEN NEW.name = 'target/1/manuallyClearedPendingRecords' BEGIN SELECT abs(-9223372036854775808); END;");
            Assert.Equal(DeliveryWriteStatus.StorageFailed, (await runtime.Deliveries.ClearPendingAsync(1)).Status);
            Assert.Equal(1025, runtime.Deliveries.GetQueueTotals(1).PendingCount);
            Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("manuallyClearedPendingRecords"));
            Sql(data.Path, "DROP TRIGGER fail_clear;");
            Assert.Equal(1025, (await runtime.Deliveries.ClearPendingAsync(1)).AffectedRecords);
            Assert.Equal(0, (await runtime.Deliveries.ClearPendingAsync(1)).AffectedRecords);
            Assert.Equal(0, runtime.Deliveries.GetStatus().DeletedRecords);
        }
        await using var reopened = RuntimeTesting.CreateRuntime([], data.Path);
        reopened.StartCore();
        Assert.Equal(1025, reopened.Deliveries.GetPersistentCounters()["manuallyClearedPendingRecords"]);
        Assert.Equal(0, reopened.Deliveries.GetPersistentCounters().GetValueOrDefault("acknowledgedRecords"));
        Assert.Empty(reopened.Deliveries.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task FailedClearReleasesTheOriginalSenderPauseWithoutResettingAutomaticSuspension()
    {
        using var data = TemporaryDirectory.Create();
        var output = new ControlledOutput();
        output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget));
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output]);
        runtime.StartCore();
        await runtime.Deliveries.EnqueueAsync([Draft(1)], Token);
        await Activate(runtime);
        await OutputPipelineTests.UntilAsync(() => Assert.Single(runtime.GetOutputSenders()).Sender.State == "automaticallyPaused");
        Sql(data.Path, "CREATE TRIGGER fail_clear BEFORE INSERT ON queue_counters WHEN NEW.name = 'manuallyClearedPendingRecords' BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await runtime.ClearPendingAsync(1, 1, Token)).Status);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Sql(data.Path, "DROP TRIGGER fail_clear;");
        Assert.Equal(1, (await runtime.ClearPendingAsync(1, 1, Token)).AffectedRecords);
        Assert.Equal("automaticallyPaused", Assert.Single(runtime.GetOutputSenders()).Sender.State);
        Assert.Equal(1, output.Sends);
        Assert.Equal(1, output.Creates);
    }

    [Fact]
    public async Task ClearRemainsAvailableUnderStorageProtectionWithoutAdmittingNewData()
    {
        using var data = TemporaryDirectory.Create();
        await using (var initial = RuntimeTesting.CreateRuntime([], data.Path))
        {
            initial.StartCore();
            Assert.True((await initial.Deliveries.EnqueueAsync([Draft(1)], Token)).Committed);
        }
        var output = new ControlledOutput();
        output.Result.TrySetResult(new(OutputSendStatus.SuspendTarget));
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, outputs: [output], configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, MinimumDiskFreeBytes = long.MaxValue });
        runtime.StartCore();
        await Activate(runtime);
        Assert.True(runtime.Deliveries.StorageProtection.Status.RejectNewRecords);
        Assert.Equal(1, (await runtime.ClearPendingAsync(1, 1, Token)).AffectedRecords);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["manuallyClearedPendingRecords"]);
        Assert.Equal(0, runtime.Deliveries.GetStatus().DeletedRecords);
    }

    private static async Task Activate(RuntimeHost runtime)
    {
        await using var prepared = runtime.Prepare(new(1, [new(RuntimeUnitId.Create("output.test", "target"), JsonSerializer.SerializeToElement(new { }))]));
        Assert.Empty(prepared.Failures);
        await runtime.ActivateAsync(prepared);
    }
    private static DeliveryDraft Draft(long target) => new(Guid.NewGuid(), target, Guid.NewGuid(), "meter", 1, "frozen-private"u8.ToArray(), "fp", "safe", "send");
    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(path, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private sealed class ControlledOutput : IOutputAdapterFactory, IOutputTarget
    {
        public string Kind => "output.test";
        public RuntimeUnitId Id => RuntimeUnitId.Create(Kind, "target");
        public long ConnectionId => 1;
        public IReadOnlyList<string> Inputs => [];
        public string? ClientId => null;
        public string Recipient => "safe";
        public string CredentialVersion => "v1";
        public string CurrentFingerprint => "fp";
        public int Sends;
        public int Creates;
        public int Disposes;
        public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OutputSendResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IOutputTarget Build(RuntimeUnitPlan plan) => this;
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => throw new NotSupportedException();
        public IOutputAdapter CreateAdapter() { Interlocked.Increment(ref Creates); return new Adapter(this); }
        private sealed class Adapter(ControlledOutput owner) : IOutputAdapter
        {
            public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref owner.Sends);
                owner.Sending.TrySetResult();
                return call == 1 ? await owner.Result.Task : new(OutputSendStatus.SuspendTarget, "test pause");
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposes); return ValueTask.CompletedTask; }
        }
    }
}

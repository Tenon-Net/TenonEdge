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

public sealed class ApplicationSenderPauseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimeUnitId InputId => RuntimeUnitId.Create("input.test", "meter/idle");

    [Theory]
    [InlineData(false, OutputSendStatus.Ack)]
    [InlineData(true, OutputSendStatus.Ack)]
    [InlineData(false, OutputSendStatus.Retry)]
    [InlineData(true, OutputSendStatus.Retry)]
    [InlineData(false, OutputSendStatus.RejectMessage)]
    [InlineData(true, OutputSendStatus.RejectMessage)]
    [InlineData(false, OutputSendStatus.SuspendTarget)]
    [InlineData(true, OutputSendStatus.SuspendTarget)]
    public async Task PausePrecedesSharedLoopStopAndWaitsForOriginalOutcomeCommit(bool coupled, OutputSendStatus outcome)
    {
        using var data = TemporaryDirectory.Create();
        var input = new InputFactory();
        var target = new Target(1, coupled);
        await using var runtime = Create(data.Path, input, target);
        await Start(runtime, target);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            Assert.False(quieting.IsCompleted);
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == InputId).State);
            Assert.Equal(0, target.Disposes);
            target.Result.TrySetResult(new(outcome, "controlled outcome", RetryAfter: TimeSpan.FromSeconds(60)));
            var quiet = await quieting.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(quiet.Succeeded);
            Assert.True(runtime.IsCurrentQuiescence(quiet));
            Assert.Equal(outcome == OutputSendStatus.Ack ? 1 : 0, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(outcome == OutputSendStatus.RejectMessage ? 1 : 0, runtime.Deliveries.ReadFailed(1, 0, 10).Count);
            Assert.Equal(outcome is OutputSendStatus.Ack or OutputSendStatus.RejectMessage ? 1 : 2,
                runtime.Deliveries.ReadPending(1, 0, 10).Count);
            await Task.Delay(300, Token); // Deliberately observe the gap between quiescence return and the later commit/restore.
            Assert.Single(target.Messages);
            Assert.Equal(coupled ? 1 : 0, target.Disposes);
            Assert.Equal(1, target.Creates);
            Assert.Equal(DeliveryWriteStatus.Rejected, (await runtime.ClearPendingAsync(1, 1, Token)).Status);
            if (outcome == OutputSendStatus.SuspendTarget)
            { Assert.Equal("automaticallyPaused", Assert.Single(runtime.GetOutputSenders()).Sender.State); }
        }
        finally { target.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCommitAbandonReleasesBarrierAndRestoresOldInputsWithoutDuplicatingIndependentSender(bool coupled)
    {
        using var data = TemporaryDirectory.Create();
        var input = new InputFactory();
        var target = new Target(1, coupled);
        await using var runtime = Create(data.Path, input, target);
        await Start(runtime, target);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            var quiet = await quieting;
            Assert.True(quiet.Succeeded);
            // A repeat attempt must retain the same barrier, including a coupled loop which is now stopped.
            var latest = await runtime.QuiesceInputsAsync([1], Token);
            Assert.True(latest.Succeeded);
            Assert.False(runtime.IsCurrentQuiescence(quiet));
            Assert.True(runtime.IsCurrentQuiescence(latest));
            Assert.Single(target.Messages);
            var recovery = await runtime.RestoreInputsAsync();
            Assert.True(recovery.Succeeded);
            await Until(() => target.Messages.Count == 2 && input.Starts == 2);
            Assert.Equal(1, runtime.ActiveRevision);
            Assert.Equal(coupled ? 2 : 1, target.Creates);
            Assert.Equal(coupled ? 1 : 0, target.Disposes);
            Assert.Equal("first", Body(target.Messages.First()));
            Assert.Equal("second", Body(target.Messages.Last()));
            Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        }
        finally { target.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutDoesNotStopInputsReplaceSenderOrLeaveAnInvisiblePause(bool coupled)
    {
        using var data = TemporaryDirectory.Create();
        var input = new InputFactory();
        var target = new Target(1, coupled) { SendTimeout = TimeSpan.FromMilliseconds(150) };
        await using var runtime = Create(data.Path, input, target);
        await Start(runtime, target);
        try
        {
            var quiet = await runtime.QuiesceInputsAsync([1], Token);
            Assert.False(quiet.Succeeded);
            Assert.Equal(target.Id, Assert.Single(quiet.TimedOutUnits));
            Assert.False(runtime.IsCurrentQuiescence(quiet));
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == InputId).State);
            Assert.Equal(1, target.Creates);
            Assert.Equal(0, target.Disposes);
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            await Until(() => target.Messages.Count == 2);
            Assert.Equal(1, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(1, target.Creates);
        }
        finally { target.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AckStorageFailureCannotBeMisreportedAsQuiescentAndIsNotResentAfterRepair(bool coupled)
    {
        using var data = TemporaryDirectory.Create();
        var input = new InputFactory();
        var target = new Target(1, coupled) { SendTimeout = TimeSpan.FromMilliseconds(500) };
        await using var runtime = Create(data.Path, input, target);
        await Start(runtime, target);
        try
        {
            Sql(data.Path, "CREATE TRIGGER fail_ack BEFORE DELETE ON delivery BEGIN SELECT abs(-9223372036854775808); END;");
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            await Until(() => target.Creates > 1);
            var rejected = await quieting;
            Assert.False(rejected.Succeeded);
            Assert.Single(target.Messages);
            Assert.Equal(0, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == InputId).State);
            Sql(data.Path, "DROP TRIGGER fail_ack");
            await Until(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            var quiet = await runtime.QuiesceInputsAsync([1], Token);
            Assert.True(quiet.Succeeded);
            Assert.Single(target.Messages, message => Body(message) == "first");
            Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        }
        finally { target.Release(); Sql(data.Path, "DROP TRIGGER IF EXISTS fail_ack"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedHandoverReusesBarrierStateAndCannotResumeOldRecipient(bool coupled)
    {
        using var data = TemporaryDirectory.Create();
        var input = new InputFactory();
        var old = new Target(1, coupled);
        var next = new Target(1, coupled, "new");
        next.Release();
        await using var runtime = Create(data.Path, input, old, next);
        await Start(runtime, old);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            old.Result.TrySetResult(new(OutputSendStatus.Ack));
            var quiet = await quieting;
            Assert.True(quiet.Succeeded);
            await using var prepared = runtime.Prepare(Plan(2, next));
            // Runtime-level caller contract only: emulate the coordinator's post-COMMIT isolation using the real writer.
            // This is NOT a config.db intent/COMMIT test and does not claim crash recovery.
            Assert.True((await runtime.Deliveries.ReconcileAsync(new Dictionary<long, string> { [1] = "new" }, false, Token)).Committed);
            await runtime.ActivateCommittedAsync(prepared, quiet);
            Assert.Equal(2, runtime.ActiveRevision);
            Assert.True((await runtime.Deliveries.EnqueueAsync([Draft(1, "fresh", "new")], Token)).Committed);
            await Until(() => next.Messages.Count == 1);
            Assert.Single(old.Messages);
            Assert.Equal(1, old.Creates);
            Assert.Equal(1, old.Disposes);
            Assert.Equal("fresh", Body(Assert.Single(next.Messages)));
            Assert.Equal("second", System.Text.Encoding.UTF8.GetString(Assert.Single(runtime.Deliveries.ReadQuarantined(1, 0, 10)).Payload.Span));
        }
        finally { old.Release(); }
    }

    [Fact]
    public async Task AllSelectedSendersArePausedBeforeWaitingForTheSlowestAndTimeoutReleasesThemTogether()
    {
        using var data = TemporaryDirectory.Create();
        var slow = new Target(1, false) { SendTimeout = TimeSpan.FromSeconds(3) };
        var fast = new Target(2, false);
        await using var runtime = Create(data.Path, new InputFactory(), slow, fast);
        await Start(runtime, slow, fast);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1, 2], Token);
            fast.Result.TrySetResult(new(OutputSendStatus.Ack));
            await Until(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            await Task.Delay(250, Token);
            Assert.Single(fast.Messages);
            Assert.False(quieting.IsCompleted);
            var quiet = await quieting;
            Assert.False(quiet.Succeeded);
            Assert.Equal(slow.Id, Assert.Single(quiet.TimedOutUnits));
            await Until(() => fast.Messages.Count == 2);
            Assert.Equal(1, fast.Creates);
            Assert.Equal(0, fast.Disposes);
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == InputId).State);
        }
        finally { slow.Release(); fast.Release(); }
    }

    [Fact]
    public async Task InputStopTimeoutAfterSuccessfulSenderPauseReleasesNewPauseWithoutReplacingOldSender()
    {
        using var data = TemporaryDirectory.Create();
        var input = new InputFactory { HoldStop = true };
        var target = new Target(1, false);
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [new Factory([target])],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(150) });
        await Start(runtime, target);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            var quiet = await quieting;
            Assert.False(quiet.Succeeded);
            Assert.Equal(InputId, Assert.Single(quiet.TimedOutUnits));
            await Until(() => target.Messages.Count == 2);
            Assert.Equal(1, target.Creates);
            Assert.Equal(0, target.Disposes);
            input.Exit.TrySetResult();
            Assert.True((await runtime.RestoreInputsAsync()).Succeeded);
        }
        finally { target.Release(); input.Exit.TrySetResult(); }
    }

    [Fact]
    public async Task FailedCommittedHandoverRetainsPauseUntilExplicitRetryOfEffectiveRevision()
    {
        using var data = TemporaryDirectory.Create();
        var old = new Target(1, false) { FailSharing = true };
        var next = new Target(1, false, "new");
        next.Release();
        await using var runtime = Create(data.Path, new InputFactory(), old, next);
        await Start(runtime, old);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            old.Result.TrySetResult(new(OutputSendStatus.Ack));
            var quiet = await quieting;
            await using var prepared = runtime.Prepare(Plan(2, next));
            // Inject a local handover exception. No configuration COMMIT or process crash is simulated by this test.
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateCommittedAsync(prepared, quiet));
            await Task.Delay(300, Token);
            Assert.Equal(2, runtime.ActiveRevision);
            Assert.Single(old.Messages);
            Assert.Equal(0, old.Disposes);
            Assert.Equal(0, next.Creates);
            old.FailSharing = false;
            var retryQuiet = await runtime.QuiesceInputsAsync(Token); // The prior barrier must survive this new lifecycle operation.
            Assert.True(retryQuiet.Succeeded);
            Assert.True((await runtime.Deliveries.ReconcileAsync(new Dictionary<long, string> { [1] = "new" }, false, Token)).Committed);
            await using var retry = runtime.Prepare(Plan(2, next));
            await runtime.RetryEffectiveAsync(retry, retryQuiet);
            Assert.True((await runtime.Deliveries.EnqueueAsync([Draft(1, "fresh", "new")], Token)).Committed);
            await Until(() => next.Messages.Count == 1);
            Assert.Single(old.Messages);
            Assert.Equal(1, old.Disposes);
            Assert.Equal("fresh", Body(Assert.Single(next.Messages)));
        }
        finally { old.Release(); }
    }

    [Fact]
    public async Task CallerCancellationLeavesOwnedPauseIntactAndShutdownWaitsForIt()
    {
        using var data = TemporaryDirectory.Create();
        var target = new Target(1, false);
        await using var runtime = Create(data.Path, new InputFactory(), target);
        await Start(runtime, target);
        try
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var quieting = runtime.QuiesceInputsAsync([1], caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quieting);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.QuiesceInputsAsync(Token));
            var stopping = runtime.StopAsync(Token);
            Assert.False(stopping.IsCompleted);
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            await stopping.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Single(target.Messages);
            Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(1, runtime.Deliveries.GetStatus().DeletedRecords);
            Assert.Equal(1, target.Creates);
            Assert.Equal(1, target.Disposes);
        }
        finally { target.Release(); }
    }

    [Fact]
    public async Task UnselectedIndependentSenderContinuesWhileSelectedSourceIsFenced()
    {
        using var data = TemporaryDirectory.Create();
        var selected = new Target(1, false);
        var unrelated = new Target(2, false);
        await using var runtime = Create(data.Path, new InputFactory(), selected, unrelated);
        await Start(runtime, selected, unrelated);
        try
        {
            var quieting = runtime.QuiesceInputsAsync([1], Token);
            selected.Result.TrySetResult(new(OutputSendStatus.Ack));
            Assert.True((await quieting).Succeeded);
            unrelated.Result.TrySetResult(new(OutputSendStatus.Ack));
            await Until(() => unrelated.Messages.Count == 2);
            Assert.Single(selected.Messages);
            Assert.Equal(1, unrelated.Creates);
            Assert.Equal(0, unrelated.Disposes);
        }
        finally { selected.Release(); unrelated.Release(); }
    }

    [Fact]
    public async Task InvalidSelectionAndPreCancelledCallerCannotStopInputsOrLeavePauses()
    {
        using var data = TemporaryDirectory.Create();
        var target = new Target(1, false);
        await using var runtime = Create(data.Path, new InputFactory(), target);
        await Start(runtime, target);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.QuiesceInputsAsync([1, 1], Token));
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.QuiesceInputsAsync([0], Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.QuiesceInputsAsync([1, 2], Token));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.QuiesceInputsAsync([1], cancelled.Token));
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == InputId).State);
            target.Result.TrySetResult(new(OutputSendStatus.Ack));
            await Until(() => target.Messages.Count == 2);
            Assert.Equal(1, target.Creates);
        }
        finally { target.Release(); }
    }

    private static string Body(OutputMessage message) => System.Text.Encoding.UTF8.GetString(message.Payload.Span);
    private static Task Until(Func<bool> condition) => OutputPipelineTests.UntilAsync(condition);
    private static RuntimeHost Create(string path, InputFactory input, params Target[] targets) =>
        RuntimeTesting.CreateRuntime([input], path, outputs: [new Factory(targets)]);
    private static RuntimePlan Plan(long revision, params Target[] targets) => new(revision,
        [new(InputId, JsonSerializer.SerializeToElement(new { })), .. targets.Select(target => new RuntimeUnitPlan(target.Id,
            JsonSerializer.SerializeToElement(new { fingerprint = target.CurrentFingerprint }))) ]);
    private static async Task Start(RuntimeHost runtime, params Target[] targets)
    {
        runtime.StartCore();
        Assert.True((await runtime.Deliveries.EnqueueAsync(targets.SelectMany(target => new[]
        { Draft(target.ConnectionId, "first"), Draft(target.ConnectionId, "second") }).ToArray(), Token)).Committed);
        await using var prepared = runtime.Prepare(Plan(1, targets));
        Assert.Empty(prepared.Failures);
        await runtime.ActivateAsync(prepared);
        await Task.WhenAll(targets.Select(target => target.Sending.Task)).WaitAsync(TimeSpan.FromSeconds(10), Token);
    }
    private static DeliveryDraft Draft(long connection, string body, string fingerprint = "old") => new(Guid.NewGuid(), connection,
        Guid.NewGuid(), "meter", 1, System.Text.Encoding.UTF8.GetBytes(body), fingerprint, "safe", "frozen/path");
    private static void Sql(string path, string sql)
    {
        using var database = new SqliteConnection($"Data Source={Path.Combine(path, "queue.db")};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private sealed class InputFactory : IInputUnitFactory
    {
        public string Kind => InputId.Kind;
        public int Starts;
        public bool HoldStop { get; init; }
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) => new Unit(this);
        private sealed class Unit(InputFactory owner) : IRuntimeUnit
        {
            public RuntimeUnitId Id => InputId;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                Interlocked.Increment(ref owner.Starts);
                try
                { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
                finally
                {
                    if (owner.HoldStop)
                    { await owner.Exit.Task; }
                }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Factory(Target[] targets) : IOutputAdapterFactory
    {
        public string Kind => "output.test";
        private Target Select(RuntimeUnitPlan plan) => targets.Single(target => target.Id == plan.Id &&
            target.CurrentFingerprint == plan.Configuration.GetProperty("fingerprint").GetString());
        public IOutputTarget Build(RuntimeUnitPlan plan) => Select(plan);
        public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => Select(plan).QuiesceWithInputs ? InputId : null;
    }
    private sealed class Target(long connection, bool coupled, string fingerprint = "old") : IOutputTarget
    {
        public RuntimeUnitId Id => RuntimeUnitId.Create("output.test", $"target{connection}");
        public long ConnectionId => connection;
        public IReadOnlyList<string> Inputs => [InputId.Key];
        public string? ClientId => null;
        public string Recipient => fingerprint;
        public string CredentialVersion => "v1";
        public string CurrentFingerprint => fingerprint;
        public bool QuiesceWithInputs => coupled;
        public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public int Creates;
        public int Disposes;
        public bool FailSharing { get; set; }
        public bool CanShareSenderWith(IOutputTarget other) => FailSharing ? throw new InvalidOperationException("injected-handover-fault") : false;
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

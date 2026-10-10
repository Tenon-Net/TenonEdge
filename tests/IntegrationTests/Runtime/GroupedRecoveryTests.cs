using System.Collections.Concurrent;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;
using static TenonEdge.IntegrationTests.Runtime.OutputPipelineTests;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class GroupedRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Keys = ["device/blocked", "device/sibling", "device/healthy"];

    [Fact]
    public async Task HealthyGroupRestartsWhileTimedOutGroupWaitsThenAutomaticallyRecoversTogether()
    {
        using var data = TemporaryDirectory.Create();
        using var inputs = new GroupInputs();
        var outputs = new GroupOutputs();
        await using var runtime = RuntimeTesting.CreateRuntime([inputs], data.Path, outputs: [outputs],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(200) });
        await using var plan = runtime.Prepare(Definition());
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 6 && outputs.Created.Count == 2);
        await inputs.BlockedReady.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False((await runtime.QuiesceInputsAsync(Token)).Succeeded);
        var attempt = await runtime.RestoreInputsAsync();
        Assert.True(attempt.Restarted);
        Assert.False(attempt.Succeeded);
        Assert.NotNull(attempt.BlockedBy);
        await UntilAsync(() => inputs.Runs.GetValueOrDefault("device/healthy") == 2 && runtime.Deliveries.GetStatus().CommittedRecords == 8);
        Assert.Equal(1, inputs.Builds["device/blocked"]);
        Assert.Equal(1, inputs.Builds["device/sibling"]); // Same receiving session: do not restart just this member.
        Assert.Equal(1, outputs.Created.Count(id => id == 11));
        inputs.Release.TrySetResult();
        await UntilAsync(() => inputs.Runs.GetValueOrDefault("device/blocked") == 2 && inputs.Runs.GetValueOrDefault("device/sibling") == 2 &&
            runtime.Deliveries.GetStatus().CommittedRecords == 12 && outputs.Created.Count(id => id == 11) == 2);
        Assert.Equal(2, inputs.Runs["device/healthy"]); // Recovery of A must not quiesce B again.
        Assert.Equal(1, outputs.Created.Count(id => id == 12));
        Assert.DoesNotContain(12L, outputs.Disposed);
        Assert.Equal(1, outputs.Disposed.Count(id => id == 11));
        Assert.All(runtime.Deliveries.ReadPending(11, 0, 20), record => Assert.Equal(1, record.Revision));
        Assert.All(runtime.Deliveries.ReadPending(12, 0, 20), record => Assert.Equal(1, record.Revision));
        // 提交计数先于请求完成通知；分发器恢复执行后才释放消息预算。
        await UntilAsync(() => runtime.InFlight.Status.ReservedBytes == 0);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.Equal(9, runtime.GetStatus().Units.Count);
    }

    [Fact]
    public async Task BuildFailureStopsAutomaticRetryAndExplicitRepairDoesNotRestartHealthyGroup()
    {
        using var data = TemporaryDirectory.Create();
        using var inputs = new GroupInputs();
        var outputs = new GroupOutputs();
        await using var runtime = RuntimeTesting.CreateRuntime([inputs], data.Path, outputs: [outputs],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(200) });
        await using var plan = runtime.Prepare(Definition());
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await inputs.BlockedReady.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        // 每个初始输入都必须真正运行过，不能把监督者已登记当成 RunAsync 已执行。
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 6 && outputs.Created.Count == 2);
        Assert.False((await runtime.QuiesceInputsAsync(Token)).Succeeded);
        Assert.False((await runtime.RestoreInputsAsync()).Succeeded);
        try
        { await UntilAsync(() => inputs.Runs.GetValueOrDefault("device/healthy") == 2); }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
        {
            Assert.Fail($"恢复计数：{JsonSerializer.Serialize(inputs.Runs)}；状态：{JsonSerializer.Serialize(runtime.GetStatus().Units)}");
        }
        inputs.RejectSibling = true;
        inputs.Release.TrySetResult();
        await UntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id == Input("sibling", "a").Id && unit.State == RuntimeUnitState.BuildFailed));
        await Task.Delay(600, Token);
        Assert.Equal(2, inputs.Builds["device/blocked"]);
        Assert.Equal(2, inputs.Builds["device/sibling"]);
        Assert.Equal(2, inputs.Builds["device/healthy"]);
        inputs.RejectSibling = false;
        Assert.True((await runtime.RestoreInputsAsync()).Succeeded);
        await UntilAsync(() => inputs.Runs.GetValueOrDefault("device/blocked") == 3 && inputs.Runs.GetValueOrDefault("device/sibling") == 2);
        Assert.Equal(2, inputs.Builds["device/healthy"]);
        Assert.Equal(1, outputs.Created.Count(id => id == 12));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrNewQuiescenceRevokesAutomaticRecoveryBeforeLateOldExit(bool quiesceAgain)
    {
        using var data = TemporaryDirectory.Create();
        using var inputs = new GroupInputs();
        await using var runtime = RuntimeTesting.CreateRuntime([inputs], data.Path,
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(200) });
        await using var plan = runtime.Prepare(Plan(Input("blocked", "a"), Input("healthy", "b")));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => inputs.Runs.Count == 2);
        await inputs.BlockedReady.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False((await runtime.QuiesceInputsAsync(Token)).Succeeded);
        Assert.False((await runtime.RestoreInputsAsync()).Succeeded);
        await UntilAsync(() => inputs.Runs["device/healthy"] == 2);
        if (quiesceAgain)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                { await runtime.QuiesceInputsAsync(deadline.Token); break; }
                catch (InvalidOperationException) { await Task.Delay(10, deadline.Token); }
            }
        }
        else
        { await runtime.StopAsync(Token); }
        inputs.Release.TrySetResult();
        await UntilAsync(() => runtime.GetStatus().Units.Single(unit => unit.Id == Input("blocked", "a").Id).State == RuntimeUnitState.Stopped);
        await Task.Delay(500, Token); // Exceeds the automatic retry interval; no worker may resurrect this generation.
        Assert.Equal(1, inputs.Builds["device/blocked"]);
        Assert.Equal(2, inputs.Builds["device/healthy"]);
        Assert.Equal(RuntimeUnitState.Stopped, runtime.GetStatus().Units.Single(unit => unit.Id == Input("healthy", "b").Id).State);
    }

    private static RuntimePlan Definition() => Plan(Input("blocked", "a"), Input("sibling", "a"), Input("healthy", "b"),
        new(RuntimeUnitId.Create("output.group", "coupled"), JsonSerializer.SerializeToElement(new { connection = 11, group = "a" })),
        new(RuntimeUnitId.Create("output.group", "independent"), JsonSerializer.SerializeToElement(new { connection = 12, group = (string?)null })));

    private static RuntimeUnitPlan Input(string name, string group) => new(RuntimeUnitId.Create("input.group", "device/" + name),
        JsonSerializer.SerializeToElement(new { group }));

    private static RuntimeUnitId Group(string key) => RuntimeUnitId.Create("connection.group", key);

    private sealed class GroupInputs : IInputUnitFactory, IDisposable
    {
        public string Kind => "input.group";
        public ConcurrentDictionary<string, int> Builds { get; } = new();
        public bool RejectSibling { get; set; }
        public ConcurrentDictionary<string, int> Runs { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan unit) => Group(unit.Configuration.GetProperty("group").GetString()!);
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            Builds.AddOrUpdate(unit.Id.Key, 1, (_, count) => count + 1);
            if (RejectSibling && unit.Id.Key == "device/sibling")
            { throw new InvalidOperationException("injected-build-failure"); }
            return new InputUnit(this, unit.Id, context);
        }
        public void Dispose() => Release.TrySetResult();
        private sealed class InputUnit(GroupInputs owner, RuntimeUnitId id, InputUnitContext context) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                var generation = owner.Runs.AddOrUpdate(id.Key, 1, (_, count) => count + 1);
                await context.Messages.ProduceAsync(1024, _ =>
                {
                    var now = context.Time.GetUtcNow();
                    return ValueTask.FromResult<EdgeMessage?>(new EdgeMessage(Guid.NewGuid(), context.InputKey, context.DeviceKey,
                        now, TimeSource.GatewayUnsynced, now, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(generation) }));
                }, stoppingToken);
                if (id.Key == "device/blocked" && generation == 1)
                { owner.BlockedReady.TrySetResult(); await owner.Release.Task; }
                else
                { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class GroupOutputs : IOutputAdapterFactory
    {
        public string Kind => "output.group";
        public ConcurrentBag<long> Created { get; } = [];
        public ConcurrentBag<long> Disposed { get; } = [];
        public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan unit) => unit.Configuration.GetProperty("group").GetString() is { } group ? Group(group) : null;
        public IOutputTarget Build(RuntimeUnitPlan plan) => new Target(this, plan);
        private sealed class Target(GroupOutputs owner, RuntimeUnitPlan plan) : IOutputTarget
        {
            public RuntimeUnitId Id => plan.Id;
            public long ConnectionId => plan.Configuration.GetProperty("connection").GetInt64();
            public IReadOnlyList<string> Inputs => Keys;
            public string? ClientId => null;
            public string Recipient => "test";
            public string CredentialVersion => "test";
            public string CurrentFingerprint => "fingerprint";
            public bool QuiesceWithInputs => owner.RecoveryGroup(plan) is not null;
            public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) =>
                new(StandardEnvelope.Serialize(message, deliveryId), "test", 1, CurrentFingerprint, "test");
            public IOutputAdapter CreateAdapter() { owner.Created.Add(ConnectionId); return new Adapter(owner, ConnectionId); }
        }
        private sealed class Adapter(GroupOutputs owner, long id) : IOutputAdapter
        {
            public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken) =>
                Task.FromResult(new OutputSendResult(OutputSendStatus.SuspendTarget, "test-paused"));
            public ValueTask DisposeAsync() { owner.Disposed.Add(id); return ValueTask.CompletedTask; }
        }
    }
}

internal sealed class RecoveryBlockedInputFactory : IInputUnitFactory, IDisposable
{
    public string Kind => "input.blocked";
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs;
    public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) => new Unit(this, plan.Id);
    public void Dispose() => Release.TrySetResult();
    private sealed class Unit(RecoveryBlockedInputFactory owner, RuntimeUnitId id) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            var generation = Interlocked.Increment(ref owner.Runs);
            owner.Started.TrySetResult();
            if (generation == 1)
            { await owner.Release.Task; }
            else
            { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

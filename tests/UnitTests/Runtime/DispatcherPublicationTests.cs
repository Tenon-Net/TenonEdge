using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>模拟唯一写者的完成信号，只测分发状态与租约生命周期，不作为 SQLite 持久化证据。</summary>
public sealed class DispatcherPublicationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(DeliveryWriteStatus.Committed)]
    [InlineData(DeliveryWriteStatus.Rejected)]
    [InlineData(DeliveryWriteStatus.StorageFailed)]
    [InlineData(DeliveryWriteStatus.Stopped)]
    public async Task FrozenSmallMessagesReturnUnusedFormattingWorkspaceButKeepOwnershipUntilWriterOutcome(DeliveryWriteStatus status)
    {
        var budget = new InFlightBudget(200032, 32);
        using var raw = budget.TryReserveBuffer(32);
        Assert.True(budget.TryReserve(7)); // Another owner must not be released.
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100000 };
        var now = DateTimeOffset.UtcNow;
        var source = new EdgeMessage(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now, []);
        Assert.True(channel.Writer.TryWrite(source));
        channel.Writer.Complete();
        var cache = new DeliveryCache(new()
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "queue.db"),
            ConfigStoreId = Guid.NewGuid().ToString("D"),
        }, TimeProvider.System);
        var state = new InputDispatcherState();
        await using var dispatcher = new InputDispatcherUnit(RuntimeUnitId.Create("input.simulator", "device/input"), channel.HoldingReader,
            [new Target(1), new Target(2)], state, 1, cache, TimeProvider.System, new Events(), channel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = dispatcher.RunAsync(stop.Token);
        var request = Assert.IsType<InsertRequest>(await cache.Requests.ReadAsync(Token));
        try
        {
            Assert.NotNull(state.Message);
            Assert.NotNull(state.Publication);
            Assert.InRange(budget.Status.ReservedBytes, source.BudgetBytes + 39 + 2 * request.Drafts.Sum(draft => draft.Payload.Length),
                source.BudgetBytes + 39 + 32768);
            Assert.Equal(0, cache.GetStatus().CommittedRecords); // No fake writer outcome yet.
            Assert.Equal(2, request.Drafts.Count);
        }
        finally
        {
            request.Complete(new(status, status == DeliveryWriteStatus.Committed ? [1, 2] : [], 0, null));
            if (status == DeliveryWriteStatus.Stopped)
            { await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            else
            { await state.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            await stop.CancelAsync();
            try
            { await running; }
            catch (OperationCanceledException) { }
            Assert.Equal(39, budget.Status.ReservedBytes);
            budget.Release(7);
            raw!.Dispose();
        }
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Theory]
    [InlineData(DeliveryWriteStatus.Committed)]
    [InlineData(DeliveryWriteStatus.Rejected)]
    [InlineData(DeliveryWriteStatus.StorageFailed)]
    [InlineData(DeliveryWriteStatus.Stopped)]
    public async Task MultiTargetReservationEndsOnlyAfterKnownWriterOutcome(DeliveryWriteStatus status)
    {
        var (cache, state, dispatcher) = Setup();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = dispatcher.RunAsync(stop.Token);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var change = cache.IdentityBarrier.EnterChangeAsync(2, wait.Token).AsTask();
        try
        {
            var request = Assert.IsType<InsertRequest>(await cache.Requests.ReadAsync(Token));
            Assert.Equal(2, request.Drafts.Count);
            Assert.False(change.IsCompleted);
            request.Complete(new(status, status == DeliveryWriteStatus.Committed ? [1, 2] : [], 0, null));
            using var writing = await change.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (status == DeliveryWriteStatus.Stopped)
            { await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            else
            { await state.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            Assert.Null(state.Publication);
            Assert.Null(state.Drafts);
            Assert.Null(state.Message);
        }
        finally
        {
            await stop.CancelAsync();
            await wait.CancelAsync();
            try
            { await running; }
            catch (OperationCanceledException) { }
            try
            { (await change).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CancellingDispatcherWaitDoesNotReleaseAnUnknownQueueOutcome()
    {
        var (cache, state, dispatcher) = Setup();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = dispatcher.RunAsync(stop.Token);
        var request = Assert.IsType<InsertRequest>(await cache.Requests.ReadAsync(Token));
        var frozen = state.Drafts;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var change = cache.IdentityBarrier.EnterChangeAsync(1, wait.Token).AsTask();
        try
        {
            Assert.False(change.IsCompleted);
            Assert.Same(frozen, state.Drafts);
            Assert.Equal(request.Drafts.Select(draft => draft.DeliveryId), state.Drafts!.Select(draft => draft.DeliveryId));
            Assert.NotNull(state.Publication); // 最终停机须先等待原写者退出，不能在分发器 Dispose 时释放。
            await dispatcher.DisposeAsync();
            Assert.False(change.IsCompleted);
            request.Complete(DeliveryWriteResult.Inserted([1, 2]));
        }
        finally
        {
            await wait.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
            state.Publication?.Dispose(); // 测试侧完成模拟写者的所有权清理。
        }
    }

    [Fact]
    public async Task RetainedBudgetSurvivesCancelledWaitAndDispatcherReconstruction()
    {
        var budget = new InFlightBudget(200_000);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100_000 };
        var (cache, state, dispatcher) = Setup(channel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = dispatcher.RunAsync(stop.Token);
        var original = Assert.IsType<InsertRequest>(await cache.Requests.ReadAsync(Token));
        var retained = budget.Status.ReservedBytes;
        Assert.InRange(retained, 1, 32768);
        var frozen = state.Drafts;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await dispatcher.DisposeAsync();
        Assert.Equal(retained, budget.Status.ReservedBytes);
        Assert.Same(frozen, state.Drafts);
        Assert.NotNull(state.Publication);
        // The first completion was not observed by the cancelled dispatcher.
        original.Complete(DeliveryWriteResult.Inserted([1, 2]));

        await using var replacement = new InputDispatcherUnit(RuntimeUnitId.Create("input.simulator", "device/input"),
            channel.HoldingReader, [new Target(1), new Target(2)], state, 1, cache, TimeProvider.System, new Events(), channel);
        using var replacementStop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var rebuilt = replacement.RunAsync(replacementStop.Token);
        var retried = Assert.IsType<InsertRequest>(await cache.Requests.ReadAsync(Token));
        try
        {
            Assert.Equal(original.Drafts.Select(draft => draft.DeliveryId), retried.Drafts.Select(draft => draft.DeliveryId));
            Assert.Same(frozen, state.Drafts);
            Assert.Equal(retained, budget.Status.ReservedBytes);
            // The real writer resolves these stable IDs idempotently; this test models its known outcome only.
            retried.Complete(DeliveryWriteResult.Inserted([1, 2]));
            await state.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(0, budget.Status.ReservedBytes);
            Assert.Null(state.Publication);
        }
        finally
        {
            await replacementStop.CancelAsync();
            try
            { await rebuilt; }
            catch (OperationCanceledException) { }
        }
    }

    private static (DeliveryCache Cache, InputDispatcherState State, InputDispatcherUnit Dispatcher) Setup(BudgetedInputChannel? channel = null)
    {
        var cache = new DeliveryCache(new()
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "queue.db"),
            ConfigStoreId = Guid.NewGuid().ToString("D"),
        }, TimeProvider.System);
        Channel<EdgeMessage> input = channel ?? (Channel<EdgeMessage>)Channel.CreateUnbounded<EdgeMessage>();
        var now = DateTimeOffset.UtcNow;
        Assert.True(input.Writer.TryWrite(new(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now, [])));
        input.Writer.Complete();
        var state = new InputDispatcherState();
        var dispatcher = new InputDispatcherUnit(RuntimeUnitId.Create("input.simulator", "device/input"), channel?.HoldingReader ?? input.Reader,
            [new Target(1), new Target(2)], state, 1, cache, TimeProvider.System, new Events(), channel);
        return (cache, state, dispatcher);
    }

    private sealed class Target(long id) : IOutputTarget
    {
        public RuntimeUnitId Id => RuntimeUnitId.Create("output.fixture", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public long ConnectionId => id;
        public IReadOnlyList<string> Inputs => ["device/input"];
        public string? ClientId => null;
        public string Recipient => "fixture";
        public string CredentialVersion => "fixture";
        public string CurrentFingerprint => "fixture";
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => new(new byte[] { 1, 2, 3 }, "events", null, "fixture", "fixture");
        public IOutputAdapter CreateAdapter() => throw new NotSupportedException();
    }

    private sealed class Events : IRuntimeEventSink
    { public void Record(RuntimeEventSeverity severity, string type, string? target, string description) { } }
}

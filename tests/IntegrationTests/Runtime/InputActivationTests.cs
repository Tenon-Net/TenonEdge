using System.Collections.Concurrent;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class InputActivationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreparingOrAbandoningAnotherPlanDoesNotReleaseOrCancelThisPlansInputs()
    {
        using var data = TemporaryDirectory.Create();
        var factory = new GatedFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path);
        var definition = OutputPipelineTests.Plan(OutputPipelineTests.Input());
        await using var first = runtime.Prepare(definition);
        await using var second = runtime.Prepare(definition);
        var contexts = factory.Contexts.ToArray();
        Assert.Equal(2, contexts.Length);
        var firstReady = contexts[0].WaitForActivationAsync(Token);
        var secondReady = contexts[1].WaitForActivationAsync(Token);
        Assert.False(firstReady.IsCompleted);
        Assert.False(secondReady.IsCompleted);
        await second.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondReady);
        Assert.False(firstReady.IsCompleted);
        runtime.StartCore();
        await runtime.ActivateAsync(first);
        await firstReady.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await factory.Running.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(secondReady.IsCanceled);
        await runtime.StopAsync(Token);
    }

    [Fact]
    public async Task FailedInputDoesNotHoldOtherInputsAtActivationBarrier()
    {
        using var data = TemporaryDirectory.Create();
        var factory = new GatedFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path);
        var good = OutputPipelineTests.Input();
        var bad = new RuntimeUnitPlan(RuntimeUnitId.Create("input.missing", "device/bad"), good.Configuration);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(good, bad));
        Assert.Single(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await factory.Running.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await runtime.StopAsync(Token);
    }

    [Fact]
    public async Task CancellingOneWaitDoesNotCancelThePlansActivation()
    {
        using var data = TemporaryDirectory.Create();
        var factory = new GatedFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input()));
        var context = Assert.Single(factory.Contexts);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelled = context.WaitForActivationAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var ready = context.WaitForActivationAsync(Token);
        Assert.False(ready.IsCompleted);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await ready.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await runtime.StopAsync(Token);
    }

    [Fact]
    public async Task ReconciliationFailureCancelsBarrierInsteadOfAllowingNetworkStartup()
    {
        using var data = TemporaryDirectory.Create();
        var factory = new GatedFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input()));
        var ready = Assert.Single(factory.Contexts).WaitForActivationAsync(Token);
        runtime.StartCore();
        runtime.Deliveries.CompleteAdding();
        await Assert.ThrowsAsync<DeliveryStoreException>(() => runtime.ActivateAsync(plan));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
        Assert.False(factory.Running.Task.IsCompleted);
        Assert.Null(runtime.ActiveRevision);
        await runtime.StopAsync(Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanProvidersCannotSilentlyReplaceAnExistingKind(bool duplicateProviders)
    {
        using var data = TemporaryDirectory.Create();
        var factory = new GatedFactory();
        var provider = new FixedProvider(factory);
        // Prepare 只装配本地对象；重复声明在任何 Build/联网前被拒绝。
        await using var runtime = RuntimeTesting.CreateRuntime(duplicateProviders ? [] : [factory], data.Path,
            planAdapters: duplicateProviders ? [provider, provider] : [provider]);
        Assert.Throws<ArgumentException>(() => runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input())));
        Assert.Empty(factory.Contexts);
    }

    private sealed class FixedProvider(IInputUnitFactory factory) : IRuntimePlanAdapterProvider
    {
        public RuntimePlanAdapters Create() => new([factory], []);
    }

    private sealed class GatedFactory : IInputUnitFactory
    {
        public string Kind => "input.test";
        public ConcurrentQueue<InputUnitContext> Contexts { get; } = new();
        public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            Contexts.Enqueue(context);
            return new Unit(this, unit.Id, context);
        }
        private sealed class Unit(GatedFactory owner, RuntimeUnitId id, InputUnitContext context) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                await context.WaitForActivationAsync(stoppingToken);
                owner.Running.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

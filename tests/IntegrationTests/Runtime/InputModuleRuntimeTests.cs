using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class InputModuleRuntimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Inputs = ["meter/module"];

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("parameter")]
    [InlineData("description")]
    public async Task RejectedSeedDoesNotAllocateAVersionConstructAnInputOrPersistModuleException(string reason)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = Path.Combine(seeds.Path, "plan.json");
        var module = new TestScalarInputModule { ThrowDescription = reason == "description" };
        var parameters = reason == "parameter" ? "{\"value\":1,\"password\":\"fixture-secret\"}" : "{\"value\":1}";
        await File.WriteAllBytesAsync(seed, Document("http://localhost/", parameters, reason == "version" ? 99 : 3), Token);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed }, services =>
            {
                if (reason != "missing")
                { services.AddSingleton<IInputModuleFactory>(module); }
            });
        Assert.Equal(RuntimeCoordinatorState.Idle, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        var state = new RuntimeStateReader(data.Path);
        Assert.Empty(state.Snapshots());
        Assert.Equal(0, module.Builds);
        Assert.Equal(0, module.Starts);
        var failure = Assert.Single(state.Events(RuntimeEventTypes.SeedRejected));
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(failure), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreezeRevalidatesAgainstThisHostsDirectoryNotTheCallersOrHistory(bool draft)
    {
        await using var host = new TenonEdgeApplicationFactory();
        var other = new InputModuleCatalog([new TestScalarInputModule()]);
        var plan = RuntimePlanDocument.Parse(Document("http://localhost/"), inputs: other);
        var store = host.Services.GetRequiredService<RuntimeVersionStore>();
        var pointers = store.ReadPointers();
        if (draft)
        {
            var version = host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version;
            Assert.Throws<RuntimePlanDocumentException>(() => store.FreezeDraft(plan, version, "tester"));
        }
        else
        { Assert.Throws<RuntimePlanDocumentException>(() => store.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "tester")); }
        Assert.Equal(pointers, store.ReadPointers());
        Assert.Null(store.Load(1));
    }

    [Fact]
    public async Task FreezeUsesDescriptionsOnlyAndPreservesExactVersionedParameters()
    {
        using var data = TemporaryDirectory.Create();
        var module = new TestScalarInputModule();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services.AddSingleton<IInputModuleFactory>(module));
        var store = host.Services.GetRequiredService<RuntimeVersionStore>();
        var plan = RuntimePlanDocument.ParseStored(Document("http://localhost/"));
        var revision = store.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "tester");
        Assert.Equal(0, module.Builds);
        Assert.Equal(0, module.Starts);
        var persisted = RuntimeSnapshotDocument.ReadPlan(store.Load(revision)!);
        Assert.Equal(plan.ContentHash, persisted.ContentHash);
        Assert.Contains("9007199254740993", persisted.CanonicalText, StringComparison.Ordinal);
        store.Abandon(revision, "test completed");
    }

    [Fact]
    public async Task OriginalRuntimeSelectsExactVersionsPreparesWithoutStartingAndRejectsBypassedAdmission()
    {
        using var data = TemporaryDirectory.Create();
        var older = new TestScalarInputModule { Version = 2 };
        var newer = new TestScalarInputModule();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton<IInputModuleFactory>(older);
            services.AddSingleton<IInputModuleFactory>(newer);
        });
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        RuntimeUnitPlan Input(int version) => new(RuntimeUnitId.Create(newer.Kind, "meter/source-" + version),
            JsonSerializer.SerializeToElement(new { inputModule = new { version, parameters = new { value = 42 } } }));
        await using (var prepared = runtime.Prepare(new(1, [Input(2), Input(3)])))
        {
            Assert.Empty(prepared.Failures);
            Assert.Equal(1, older.Builds);
            Assert.Equal(1, newer.Builds);
            Assert.Equal(0, older.Starts + newer.Starts);
        }
        Assert.Equal(1, older.Disposed);
        Assert.Equal(1, newer.Disposed);
        await using var rejected = runtime.Prepare(new(2, [Input(99)]));
        Assert.Single(rejected.Failures);
        Assert.Equal(1, older.Builds);
        Assert.Equal(1, newer.Builds);
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task DurableModuleWithoutAnOutputGetsExplicitRejectionThroughTheOriginalChannel()
    {
        using var data = TemporaryDirectory.Create();
        var module = new TestScalarInputModule { UsesDurableHandoff = true };
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services.AddSingleton<IInputModuleFactory>(module));
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var input = RuntimePlanDocument.Parse(Document("http://localhost/"), inputs: runtime.InputModules).Units.Single(unit => unit.Id.Kind == module.Kind);
        await using var prepared = runtime.Prepare(new(1, [input]));
        Assert.Empty(prepared.Failures);
        await runtime.ActivateAsync(prepared);
        Assert.Equal(InputHandoffStatus.Rejected, await module.Handoff.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
        Assert.Equal(input.Id, module.RecoveryId);
        Assert.Equal("9007199254740993", module.RecoveryParameters.GetProperty("value").GetRawText());
        Assert.False(module.RecoveryParameters.TryGetProperty("inputModule", out _));
    }

    internal static byte[] Document(string address, string parameters = "{\"value\":9007199254740993}", int version = 3) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        units = new object[]
        {
            new { kind = "input.simulator", key = "healthy/sample", configuration = new { periodMs = 1000, seed = 42 } },
            new { kind = "input.testscalar", key = "meter/module", configuration = new { inputModule = new { version, parameters = JsonSerializer.Deserialize<JsonElement>(parameters) } } },
            new { kind = "output.http", key = "sink", configuration = new { connectionId = 1, baseAddress = address, path = "data", inputs = Inputs } },
        }
    });
}

/// <summary>测试侧独立采样算法，不借用内置模拟器；实例进入原通道、监督者与发送队列。</summary>
internal sealed class TestScalarInputModule : IInputModuleFactory
{
    private int _builds;
    private int _starts;
    private int _disposed;
    public int Builds => Volatile.Read(ref _builds);
    public int Starts => Volatile.Read(ref _starts);
    public int Disposed => Volatile.Read(ref _disposed);
    public string Kind => "input.testscalar";
    public int Version { get; init; } = 3;
    public string DisplayName => "测试标量输入";
    public bool Emit { get; init; } = true;
    public bool UsesDurableHandoff { get; init; }
    public TaskCompletionSource<InputHandoffStatus> Handoff { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RuntimeUnitId? RecoveryId { get; private set; }
    public JsonElement RecoveryParameters { get; private set; }
    public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan unit)
    {
        RecoveryId = unit.Id;
        RecoveryParameters = unit.Configuration.Clone();
        return unit.Id;
    }
    public bool ThrowDescription { get; init; }
    public Action? OnDescribe { get; init; }
    public bool ThrowBuild { get; init; }
    public ParameterSchema Parameters { get; } = new("input.testscalar", ParameterScope.Input,
        [new("value", "初值", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "0", MaximumJson = "9223372036854775807" }]);
    public ImmutableArray<InputModuleField> Describe(JsonElement parameters)
    {
        OnDescribe?.Invoke();
        return ThrowDescription ? throw new InvalidOperationException("fixture-secret") : [new("value", FieldType.Integer, "count")];
    }
    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
    {
        Assert.False(unit.Configuration.TryGetProperty("inputModule", out _));
        Interlocked.Increment(ref _builds);
        if (ThrowBuild)
        { throw new InvalidOperationException("fixture-secret"); }
        return new Unit(this, unit.Id, context, unit.Configuration.GetProperty("value").GetInt64());
    }
    private sealed class Unit(TestScalarInputModule owner, RuntimeUnitId id, InputUnitContext context, long value) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            await context.WaitForActivationAsync(stoppingToken);
            Interlocked.Increment(ref owner._starts);
            if (owner.Emit)
            {
                ValueTask<EdgeMessage?> Sample(CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    var at = context.Time.GetUtcNow();
                    return ValueTask.FromResult<EdgeMessage?>(new(Guid.CreateVersion7(at), context.InputKey, context.DeviceKey,
                        at, context.GatewayTimeSource, at, [new("value", FieldValue.FromInt64(value))]));
                }
                if (owner.UsesDurableHandoff)
                { owner.Handoff.TrySetResult(await context.Messages.ProduceConfirmedAsync(4096, 4096, Sample, stoppingToken)); }
                else
                { await context.Messages.ProduceAsync(4096, Sample, stoppingToken); }
                context.Reads.ReadCompleted(context.Time.GetUtcNow(), 0);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner._disposed);
            return ValueTask.CompletedTask;
        }
    }
}

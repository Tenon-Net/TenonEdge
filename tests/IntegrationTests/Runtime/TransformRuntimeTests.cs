using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class TransformRuntimeTests
{
    private static readonly string[] Inputs = ["meter/sim"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CustomTransformAndMappingShareOriginalDispatcherQueueAndIdentityWhileExceptionsRejectOnlyTheirPath()
    {
        using var data = TemporaryDirectory.Create();
        var events = new RecordingEventSink();
        var input = new OneShotInputFactory(3);
        var extension = new TestOffsetTransformFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, events,
            outputs: [OutputFactory()], transforms: new([extension]));
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(),
            Output(1, 1, map: true), Output(2, 13), Output(3, 2)));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 6 && runtime.InFlight.Status.ReservedBytes == 0);
        await runtime.StopAsync(Token);
        var first = runtime.Deliveries.ReadPending(1, 0, 10);
        var third = runtime.Deliveries.ReadPending(3, 0, 10);
        Assert.Equal(3, first.Count);
        Assert.Empty(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Equal(first.Select(item => item.SourceMessageId), third.Select(item => item.SourceMessageId));
        for (var i = 0; i < 3; i++)
        {
            using var json = JsonDocument.Parse(first[i].Payload);
            Assert.Equal(43 + i, json.RootElement.GetProperty("values").GetProperty("final").GetInt64());
            using var other = JsonDocument.Parse(third[i].Payload);
            Assert.Equal(44 + i, other.RootElement.GetProperty("values").GetProperty("value").GetInt64());
        }
        Assert.Equal(3, runtime.Deliveries.GetPersistentCounters()["path/meter/sim/2/dataRejected"]);
        Assert.Equal(1, input.Started);
        Assert.Equal(9, extension.Calls);
        Assert.DoesNotContain(events.Events, item => item.Description.Contains("fixture-secret", StringComparison.Ordinal));
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task FortyEightStagesAndThirtyTwoPathsUseOneSerialWorkspace()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var extension = new TestOffsetTransformFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [OutputFactory()], transforms: new([extension]));
        var stages = Enumerable.Repeat(Stage(1), 48).ToArray();
        var outputs = Enumerable.Range(1, 32).Select(id => Output(id, stages));
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan([OutputPipelineTests.Input(), .. outputs]));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 32);
        await runtime.StopAsync(Token);
        foreach (var id in Enumerable.Range(1, 32))
        {
            using var json = JsonDocument.Parse(Assert.Single(runtime.Deliveries.ReadPending(id, 0, 2)).Payload);
            Assert.Equal(90, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
        }
        Assert.Equal(48 * 32, extension.Calls);
        Assert.Equal(1, input.Started);
        Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 1, 64L * 1024 * 1024);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task FrozenRecordsAreNotConvertedAgainAfterRestartOrNewConfiguration()
    {
        using var data = TemporaryDirectory.Create();
        byte[] frozen;
        Guid id;
        await using (var first = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [OutputFactory()], transforms: new([new TestOffsetTransformFactory()])))
        {
            await using var plan = first.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output(1, 1)));
            Assert.Empty(plan.Failures);
            first.StartCore();
            await first.ActivateAsync(plan);
            await OutputPipelineTests.UntilAsync(() => first.Deliveries.GetStatus().CommittedRecords == 1);
            await first.StopAsync(Token);
            var record = Assert.Single(first.Deliveries.ReadPending(1, 0, 2));
            frozen = record.Payload.ToArray();
            id = record.DeliveryId;
        }
        var extension = new TestOffsetTransformFactory();
        await using var second = RuntimeTesting.CreateRuntime([new OneShotInputFactory(0)], data.Path, outputs: [OutputFactory()], transforms: new([extension]));
        await using var next = second.Prepare(new(2, [OutputPipelineTests.Input(), Output(1, 13)])); // Would throw if reprocessed.
        Assert.Empty(next.Failures);
        second.StartCore();
        await second.ActivateAsync(next);
        await second.StopAsync(Token);
        var recovered = Assert.Single(second.Deliveries.ReadPending(1, 0, 2));
        Assert.Equal(id, recovered.DeliveryId);
        Assert.Equal(frozen, recovered.Payload.ToArray());
        Assert.Equal(0, extension.Calls);
    }

    [Theory]
    [InlineData("output.http")]
    [InlineData("output.mqtt")]
    public void BothProtocolSnapshotsValidateRegisteredModulesBeforeReturningANewPlan(string kind)
    {
        var catalog = new TransformCatalog([new TestOffsetTransformFactory()]);
        var bytes = Document(kind, Stage(1));
        Assert.NotNull(RuntimePlanDocument.Parse(bytes, catalog));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(bytes));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Document(kind, Stage(1, version: 2)), catalog));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Document(kind,
            JsonSerializer.Deserialize<JsonElement>("""{"kind":"transform.test-offset","version":1,"configuration":{"offset":1,"password":"fixture-secret"}}""")), catalog));
        var stored = RuntimePlanDocument.ParseStored(bytes);
        Assert.Equal(RuntimePlanDocument.Parse(bytes, catalog).ContentHash, stored.ContentHash);
    }

    [Theory]
    [InlineData("output.http")]
    [InlineData("output.mqtt")]
    public void QueueOnlyOutputsCannotHideTransformConfiguration(string kind)
    {
        var settings = new Dictionary<string, object?> { ["connectionId"] = 1, ["queueOnly"] = true, ["transformStages"] = new[] { Stage(1) } };
        settings[kind == "output.http" ? "baseAddress" : "host"] = kind == "output.http" ? "http://localhost/" : "localhost";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units = new[] { new { kind, key = "queue", configuration = settings } } });
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(bytes, new([new TestOffsetTransformFactory()])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryFreezeRevalidatesHistoryOrDocumentsFromAnotherCatalogBeforeWriting(bool draft)
    {
        await using var host = new TenonEdgeApplicationFactory();
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var document = RuntimePlanDocument.ParseStored(Document("output.http", Stage(1)));
        var before = versions.ReadPointers();
        if (draft)
        {
            var version = host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version;
            Assert.Throws<RuntimePlanDocumentException>(() => versions.FreezeDraft(document, version, "tester"));
        }
        else
        { Assert.Throws<RuntimePlanDocumentException>(() => versions.Freeze(document, RuntimeSnapshotOrigins.Seed, null, "tester")); }
        Assert.Equal(before, versions.ReadPointers());
        Assert.Null(versions.Load(1));
    }

    [Fact]
    public async Task CompleteDraftSnapshotAlsoPreservesCustomStagesAndValidatesOnRefreeze()
    {
        using var data = TemporaryDirectory.Create();
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
            services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory())))
        {
            var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
            var catalog = host.Services.GetRequiredService<TransformCatalog>();
            var plan = RuntimePlanDocument.Parse(Document("output.http", Stage(1)), catalog);
            var version = host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version;
            var revision = versions.FreezeDraft(plan, version, "tester");
            var snapshot = versions.Load(revision)!;
            Assert.Equal(2, snapshot.SchemaVersion);
            var stored = RuntimeSnapshotDocument.Read(snapshot);
            Assert.NotNull(stored.Draft);
            Assert.Equal(plan.ContentHash, stored.Plan.ContentHash);
            Assert.Equal(plan.ContentHash, RuntimeSnapshotDocument.ReadExecutionPlan(snapshot).ContentHash);
            versions.Abandon(revision, "test complete");
        }
        await using var without = new FixedDataDirectoryApplicationFactory(data.Path);
        var nextVersions = without.Services.GetRequiredService<RuntimeVersionStore>();
        var historical = RuntimeSnapshotDocument.ReadPlan(nextVersions.Load(1)!);
        var nextVersion = without.Services.GetRequiredService<ConnectionDraftStore>().Read().Version;
        Assert.Throws<RuntimePlanDocumentException>(() => nextVersions.FreezeDraft(historical, nextVersion, "tester"));
        Assert.Null(nextVersions.Load(2));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("parameter")]
    [InlineData("build")]
    public async Task InvalidSeedCannotPersistSecretsOrAllocateAVersion(string reason)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = Path.Combine(seeds.Path, "plan.json");
        var stage = reason == "parameter" ? JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"transform.test-offset","version":1,"configuration":{"offset":1,"password":"fixture-secret"}}""") : Stage(1, reason == "version" ? 2 : 1);
        await File.WriteAllBytesAsync(seed, Document("output.http", stage), Token);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed }, services =>
            {
                if (reason != "missing")
                { services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory { ThrowBuild = reason == "build" }); }
            });
        Assert.Equal(RuntimeCoordinatorState.Idle, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        var state = new RuntimeStateReader(data.Path);
        Assert.Empty(state.Snapshots());
        var error = Assert.Single(state.Events(RuntimeEventTypes.SeedRejected));
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredHostSeedPersistsAndRestartsButMissingModuleStopsInputsWithoutDamagingHistory()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = Path.Combine(seeds.Path, "plan.json");
        await File.WriteAllBytesAsync(seed, Document("output.http", Stage(1), includeInput: true), Token);
        string content;
        string hash;
        byte[] body;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed }, services =>
            {
                services.AddSingleton<IInputUnitFactory>(new OneShotInputFactory());
                services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory());
            }))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Same(host.Services.GetRequiredService<TransformCatalog>(), runtime.Transforms);
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 1);
            var snapshot = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            (content, hash) = (snapshot.Content, snapshot.ContentHash);
            body = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2)).Payload.ToArray();
        }
        var missingInput = new OneShotInputFactory();
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services.AddSingleton<IInputUnitFactory>(missingInput)))
        {
            var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
            Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
            Assert.Contains("转换模块", coordinator.Status.Reason, StringComparison.Ordinal);
            Assert.Equal(0, missingInput.Started);
            var snapshot = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            Assert.Equal(content, snapshot.Content);
            Assert.Equal(hash, snapshot.ContentHash);
            Assert.Equal(hash, RuntimeSnapshotDocument.ReadPlan(snapshot).ContentHash);
            var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
            var pointers = versions.ReadPointers();
            Assert.Throws<RuntimePlanDocumentException>(() => versions.ReadRetryCheckpoint(1, hash));
            Assert.Equal(pointers, versions.ReadPointers());
            Assert.Equal(body, Assert.Single(host.Services.GetRequiredService<RuntimeHost>().Deliveries.ReadPending(1, 0, 2)).Payload.ToArray());
        }
        var extension = new TestOffsetTransformFactory();
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
            {
                services.AddSingleton<IInputUnitFactory>(new OneShotInputFactory(0));
                services.AddSingleton<IMessageTransformFactory>(extension);
            }))
        {
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(body, Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2)).Payload.ToArray());
            Assert.Equal(0, extension.Calls);
            Assert.Equal(hash, host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
        }
    }

    internal static JsonElement Stage(int offset, int version = 1) => JsonSerializer.SerializeToElement(new
    { kind = "transform.test-offset", version, configuration = new { offset } });

    private static RuntimeUnitPlan Output(int id, int offset, bool map = false) => Output(id, map ? [Stage(offset),
        JsonSerializer.Deserialize<JsonElement>("""{"kind":"transform.map","version":1,"configuration":{"rules":[{"target":"final","type":"integer","sourceField":"value"}]}}""")] : [Stage(offset)]);
    private static RuntimeUnitPlan Output(int id, JsonElement[] stages) => new(RuntimeUnitId.Create("output.http", "out-" + id),
        JsonSerializer.SerializeToElement(new { connectionId = id, baseAddress = "http://localhost/", inputs = Inputs, transformStages = stages }));
    private static byte[] Document(string kind, JsonElement stage, bool includeInput = false)
    {
        var configuration = new Dictionary<string, object?> { ["connectionId"] = 1, ["inputs"] = Inputs, ["transformStages"] = new[] { stage } };
        if (kind == "output.http")
        { configuration["baseAddress"] = "http://localhost/"; }
        else
        { configuration["host"] = "localhost"; configuration["topic"] = "test"; }
        var units = new List<object>();
        if (includeInput)
        { units.Add(new { kind = "input.test", key = "meter/sim", configuration = new { } }); }
        units.Add(new { kind, key = "out", configuration });
        return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units });
    }
    private static HttpOutputFactory OutputFactory() => new(new Anonymous(), TimeProvider.System, new DeniedConnector(), new SharedConnections());
    private sealed class Anonymous : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private sealed class DeniedConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new OutboundAccessDeniedException();
    }
}

// Test-only, independently implemented scalar transform; not lowered to a FieldMapping rule.
internal sealed class TestOffsetTransformFactory : IMessageTransformFactory
{
    private int _calls;
    public string Kind => "transform.test-offset";
    public string DisplayName => "测试增量";
    public int Version => 1;
    public bool ThrowBuild { get; init; }
    public int Calls => Volatile.Read(ref _calls);
    public ParameterSchema? Parameters { get; } = new("test-offset", ParameterScope.Transform,
        [new("offset", "增量", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "-100", MaximumJson = "100", DefaultJson = "1" }]);
    public IMessageTransform Build(JsonElement configuration) => ThrowBuild ? throw new InvalidOperationException("fixture-secret") : new Offset(this, configuration.GetProperty("offset").GetInt64());
    private sealed class Offset(TestOffsetTransformFactory owner, long offset) : IMessageTransform
    {
        public ImmutableArray<TransformField> Schema => [new("value", FieldType.Integer, null)];
        public ImmutableArray<string> ReferencedFields => ["value"];
        public long MaximumResultBytes => 4096;
        public long WorkingReservationBytes => 8192;
        public MessageTransformResult Apply(EdgeMessage source)
        {
            Interlocked.Increment(ref owner._calls);
            if (offset == 13)
            { throw new InvalidOperationException("fixture-secret"); }
            if (!source.Values.TryGetValue("value", out var value) || !value.TryGetExactNumber(out var number))
            { return new(null, MissingFields: 1); }
            return new(new(source.SourceMessageId, source.InputId, source.DeviceKey, source.CollectedAt, source.TimeSource, source.ReceivedAt,
                new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(checked((long)number + offset)) },
                rawJson: source.RawJson, diagnosticAccess: source.DiagnosticAccess, sampleMetadata: source.SampleMetadata));
        }
    }
}

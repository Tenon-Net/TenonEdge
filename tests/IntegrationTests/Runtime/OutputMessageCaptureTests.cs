using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Outputs;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputMessageCaptureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly RuntimeUnitId Input = RuntimeUnitId.Create("input.test", "meter/sim");
    private static EdgeMessage Message()
    {
        var at = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "sim", "meter", at, TimeSource.GatewayUnsynced, at,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(long.MaxValue) });
    }

    [Fact]
    public async Task EarlierQuotaRejectionCannotBeMistakenForLaterCommittedRecord()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumPendingPerTarget = 1,
        });
        runtime.StartCore();
        Assert.True((await runtime.Deliveries.EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), "fingerprint", "fixture", "topic", 1)], Token)).Committed);
        var blocked = new Target("first", 1);
        var accepted = new Target("second", 2);
        var source = Message();
        await Dispatch(runtime.Deliveries, runtime.Messages, source, [blocked, accepted]);
        var rejected = Assert.Single(runtime.Messages.Read(blocked.Id, true));
        var queued = Assert.Single(runtime.Messages.Read(accepted.Id, true));
        Assert.Equal(RuntimeMessageOutcome.QuotaRejected, rejected.Details!.Outcome);
        Assert.Equal(RuntimeMessageOutcome.Queued, queued.Details!.Outcome);
        Assert.Equal(source.SourceMessageId, queued.SourceMessageId);
        var stored = Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Equal(stored.DeliveryId, queued.Details.DeliveryId);
        Assert.Equal(stored.SourceMessageId, queued.SourceMessageId);
        Assert.NotEqual(stored.DeliveryId, rejected.Details.DeliveryId);
        using var json = JsonDocument.Parse(queued.Body!);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(stored.Payload.Span), json.RootElement.GetProperty("output").GetProperty("body").GetString());
        Assert.Contains("9223372036854775807", queued.Body!, StringComparison.Ordinal);
        Assert.Null(Assert.Single(runtime.Messages.Read(accepted.Id, false)).Body);
    }

    [Fact]
    public async Task MappingAndFormattingFailuresAreVisibleWithoutRejectingTheOtherPath()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var missing = new Target("missing", 1);
        var format = new Target("format", 2, rejectFormat: true);
        var accepted = new Target("accepted", 3);
        var mappings = new Dictionary<RuntimeUnitId, TransformPipeline>
        {
            [missing.Id] = new([new FieldMappingTransform(new([new("required", FieldType.Integer, SourceField: "absent", Required: true)]))]),
            [accepted.Id] = new([new FieldMappingTransform(new([new("exact", FieldType.Integer, SourceField: "value"), new("optional", FieldType.String, SourceField: "absent")]))]),
        };
        await Dispatch(runtime.Deliveries, runtime.Messages, Message(), [missing, format, accepted], mappings);
        var missingRecord = Assert.Single(runtime.Messages.Read(missing.Id, true));
        Assert.Equal(RuntimeMessageOutcome.MappingRejected, missingRecord.Details!.Outcome);
        Assert.Equal(0, missingRecord.Details.FailedMappingStage);
        Assert.Equal(1, missingRecord.Details.MissingFields);
        Assert.Equal(RuntimeMessageOutcome.FormattingRejected, Assert.Single(runtime.Messages.Read(format.Id, false)).Details!.Outcome);
        var successful = Assert.Single(runtime.Messages.Read(accepted.Id, true));
        Assert.Equal(RuntimeMessageOutcome.Queued, successful.Details!.Outcome);
        Assert.Equal(1, successful.Details.MissingFields);
        Assert.Contains("exact", successful.Body!, StringComparison.Ordinal);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Empty(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Single(runtime.Deliveries.ReadPending(3, 0, 10));
    }

    [Theory]
    [InlineData("protection", RuntimeMessageOutcome.StorageProtected)]
    [InlineData("storage", RuntimeMessageOutcome.StorageFailed)]
    [InlineData("constraint", RuntimeMessageOutcome.DataRejected)]
    public async Task ProtectionAndFailedTransactionsNeverReportQueued(string failure, RuntimeMessageOutcome expected)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MinimumDiskFreeBytes = failure == "protection" ? long.MaxValue : 0,
        });
        runtime.StartCore();
        if (failure != "protection")
        {
            _ = runtime.Deliveries.GetPersistentCounters();
            using var db = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = failure == "storage"
                ? "CREATE TRIGGER fail_diagnostic_insert BEFORE INSERT ON delivery BEGIN SELECT * FROM test_missing_table; END"
                : "CREATE TRIGGER fail_diagnostic_insert BEFORE INSERT ON delivery BEGIN SELECT RAISE(ABORT, 'test-insert-failure'); END";
            command.ExecuteNonQuery();
        }
        var first = new Target("first", 1);
        var second = new Target("second", 2);
        await Dispatch(runtime.Deliveries, runtime.Messages, Message(), [first, second]);
        foreach (var target in new[] { first, second })
        {
            var record = Assert.Single(runtime.Messages.Read(target.Id, true));
            Assert.Equal(expected, record.Details!.Outcome);
            Assert.NotNull(record.Body);
            Assert.Empty(runtime.Deliveries.ReadPending(target.ConnectionId, 0, 10));
        }
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task DiagnosticEncodingFailureCannotAlterFormalQueueHandoff()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        // Test adapter deliberately returns binary text. Formal queue permits bytes;
        // diagnostic JSON must not replace them with a different, apparently valid text.
        var target = new Target("invalid-utf8", 1, invalidUtf8: true);
        await Dispatch(runtime.Deliveries, runtime.Messages, Message(), [target]);
        Assert.Equal(new byte[] { 0xff }, Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10)).Payload.ToArray());
        Assert.Empty(runtime.Messages.Read(target.Id, true));
        await runtime.StopAsync(Token);
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["debugCaptureFailures"]);
        Assert.Equal(0, runtime.Deliveries.GetActivityTotals().RejectedPaths);
    }

    [Fact]
    public async Task RuntimeUsesItsOriginalCacheAndTransitionReleasesRemovedObjectsButKeepsHistoricalRevisions()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [new Factory()]);
        var originalCache = runtime.Messages;
        var firstOutput = Definition("first", 1);
        var secondOutput = Definition("second", 2);
        await using var first = runtime.Prepare(new(1, [OutputPipelineTests.Input(), firstOutput, secondOutput]));
        Assert.Empty(first.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(first);
        await OutputPipelineTests.UntilAsync(() => runtime.Messages.Read(secondOutput.Id, false).Any(record => record.Details?.Outcome == RuntimeMessageOutcome.Queued));
        Assert.Single(runtime.Messages.Read(Input, false));
        var quiet = await runtime.QuiesceInputsAsync(Token);
        Assert.True(quiet.Succeeded);
        await using var next = runtime.Prepare(new(2, [OutputPipelineTests.Input(), secondOutput]));
        Assert.Empty(next.Failures);
        await runtime.ActivateCommittedAsync(next, quiet);
        await OutputPipelineTests.UntilAsync(() => runtime.Messages.Read(secondOutput.Id, false).Any(record => record.Revision == 2 && record.Details?.Outcome == RuntimeMessageOutcome.Queued));
        Assert.Same(originalCache, runtime.Messages);
        Assert.Empty(runtime.Messages.Read(firstOutput.Id, true));
        Assert.Equal(new long[] { 2, 1 }, runtime.Messages.Read(secondOutput.Id, false).Select(record => record.Revision));
        Assert.Equal(new long[] { 2, 1 }, runtime.Messages.Read(Input, false).Select(record => record.Revision));
        var finalQuiet = await runtime.QuiesceInputsAsync(Token);
        await using var empty = runtime.Prepare(new(3, []));
        await runtime.ActivateCommittedAsync(empty, finalQuiet);
        Assert.Equal(new RuntimeMessageCacheStatus(0, 0, 0, 0), runtime.Messages.Status);
        // Removing diagnostics never deletes formal delivery records.
        Assert.Equal(3, runtime.Deliveries.GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task MultipleCompiledPathsShareTheLogicalOutputRecordBudget()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory(55)], data.Path, outputs: [new Factory()]);
        var left = Definition("route/left", 1);
        var right = Definition("route/right", 2);
        var output = RuntimeUnitId.Create("output.test", "route/node");
        var objects = new Dictionary<RuntimeUnitId, RuntimeUnitId> { [left.Id] = output, [right.Id] = output };
        await using var first = runtime.Prepare(new(1, [OutputPipelineTests.Input(), left, right], objects));
        runtime.StartCore();
        await runtime.ActivateAsync(first);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 110 && runtime.InFlight.Status.ReservedBytes == 0);
        var records = runtime.Messages.Read(output, false);
        Assert.Equal(100, records.Count);
        Assert.Equal(50, records.Count(record => record.Details!.PathKey == left.Id.Key));
        Assert.Equal(50, records.Count(record => record.Details!.PathKey == right.Id.Key));
        Assert.All(records, record => Assert.Equal(RuntimeMessageOutcome.Queued, record.Details!.Outcome));
        Assert.Empty(runtime.Messages.Read(left.Id, true));
        Assert.Empty(runtime.Messages.Read(right.Id, true));
        var quiet = await runtime.QuiesceInputsAsync(Token);
        await using var next = runtime.Prepare(new(2, [OutputPipelineTests.Input(), right], new Dictionary<RuntimeUnitId, RuntimeUnitId> { [right.Id] = output }));
        await runtime.ActivateCommittedAsync(next, quiet);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 165 && runtime.InFlight.Status.ReservedBytes == 0);
        records = runtime.Messages.Read(output, false);
        Assert.Equal(100, records.Count);
        Assert.Equal(55, records.Count(record => record.Revision == 2));
        Assert.Equal(45, records.Count(record => record.Revision == 1));
        Assert.Equal(2, runtime.Messages.Status.Objects); // one source and one logical output
        var catalog = runtime.GetMessageCatalog();
        Assert.Equal(2, catalog.Revision);
        Assert.Equal(2, catalog.Objects.Length);
        Assert.Contains(output, catalog.Objects);
        Assert.DoesNotContain(left.Id, catalog.Objects);
        Assert.DoesNotContain(right.Id, catalog.Objects);
    }

    private static async Task Dispatch(DeliveryCache deliveries, RuntimeMessageCache messages, EdgeMessage source,
        IReadOnlyList<IOutputTarget> targets, IReadOnlyDictionary<RuntimeUnitId, TransformPipeline>? mappings = null)
    {
        var channel = Channel.CreateBounded<EdgeMessage>(1);
        var state = new InputDispatcherState();
        await using var dispatcher = new InputDispatcherUnit(Input, channel.Reader, targets, state, 1, deliveries,
            TimeProvider.System, new RecordingEventSink(), mappings: mappings, messages: messages);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = dispatcher.RunAsync(stop.Token);
        try
        {
            await channel.Writer.WriteAsync(source, Token);
            channel.Writer.Complete();
            await state.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    private static RuntimeUnitPlan Definition(string key, long connection) => new(RuntimeUnitId.Create("output.test", key),
        JsonSerializer.SerializeToElement(new { connection }));
    private sealed class Factory : IOutputAdapterFactory
    {
        public string Kind => "output.test";
        public IOutputTarget Build(RuntimeUnitPlan plan) => new Target(plan.Id.Key, plan.Configuration.GetProperty("connection").GetInt64());
    }
    private sealed class Target(string key, long connection, bool rejectFormat = false, bool invalidUtf8 = false) : IOutputTarget
    {
        public RuntimeUnitId Id { get; } = RuntimeUnitId.Create("output.test", key);
        public long ConnectionId => connection;
        public string ClientId => "client-" + connection;
        public IReadOnlyList<string> Inputs => [Input.Key];
        public string Recipient => "mqtt://fixture.test:1883";
        public string CurrentFingerprint => "fingerprint";
        public string CredentialVersion => "version";
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => rejectFormat
            ? throw new ArgumentException("fixture-format-rejection")
            : new(invalidUtf8 ? new byte[] { 0xff } : StandardEnvelope.Serialize(message, deliveryId), "topic", 1, CurrentFingerprint, "fixture");
        public IOutputAdapter CreateAdapter() => new PausedAdapter();
    }
    private sealed class PausedAdapter : IOutputAdapter
    {
        public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken) => Task.FromResult(new OutputSendResult(OutputSendStatus.SuspendTarget, "fixture-pause"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

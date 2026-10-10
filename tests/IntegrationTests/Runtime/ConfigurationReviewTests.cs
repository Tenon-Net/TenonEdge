using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationReviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly long[] TargetConnections = [1, 2];
    private static readonly long[] MissingSourceIds = [1];
    private static readonly string[] Inputs = ["device/input"];
    private static RuntimePlanDocument Empty => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Fact]
    public async Task ReviewReadsFullDraftChangesWithoutAllocatingVersionsOrChangingRuntime()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator, Enabled: false), "operator", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.ReadPointers();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var status = coordinator.Status;
        var review = versions.ReviewDraft(saved.Version, Token);
        Assert.True(review.PreflightPassed);
        Assert.True(review.ComparisonAvailable);
        Assert.Null(review.EffectiveRevision);
        Assert.Equal(saved.Version, review.Version);
        var change = Assert.Single(review.Changes);
        Assert.Equal(("device", "added", saved.Device.Id), (change.Kind, change.Change, change.Id));
        Assert.False(change.After!.Value.GetProperty("enabled").GetBoolean());
        Assert.Contains("configuration.allInputsPause", review.Warnings);
        Assert.Contains("configuration.mqttQos1RequiresPersistentBrokerSession", review.Warnings);
        Assert.Contains("configuration.qos0AndPollingGap", review.Warnings);
        Assert.Contains("configuration.queuedMessagesKeepOriginalContent", review.Warnings);
        Assert.Equal(before, versions.ReadPointers());
        Assert.Empty(versions.ReadHistory());
        Assert.Equal(status, coordinator.Status);
        Assert.Empty(coordinator.ReadApplications());
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task CompleteSavedRouteUsesTheSameCompilerWithoutStartingItsInputsOrOutputs()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new("0", "meter", "Meter", DeviceKind.Simulator), "operator", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"value\":42}")), "operator", null);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(input.Version, "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "operator", null);
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("in", "out")], new([]));
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var saved = routes.Write(null, new(connection.Version, "route", "Route", true, graph), "operator", null);
        var expected = RoutePlanCompiler.Compile(routes.ReadCompilation(saved.Version, Token), Token);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var review = versions.ReviewDraft(saved.Version, Token);
        Assert.True(review.PreflightPassed);
        Assert.Equal(expected.Document.ContentHash, review.PlanHash);
        Assert.Equal(4, review.Changes.Length);
        Assert.Empty(versions.ReadHistory());
        Assert.Null(factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task ComparisonUsesTheEffectiveCheckpointAndPreservesDetachedReviewAfterEdits()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var original = devices.Write(null, new("0", "meter", "Original", DeviceKind.Simulator), "operator", null);
        versions.Commit(versions.FreezeDraft(Empty, original.Version, "operator"), null);
        Assert.Empty(versions.ReviewDraft(original.Version, Token).Changes);
        var edited = devices.Write(original.Device.Id, new(original.Version, "meter", "Edited", DeviceKind.Simulator), "editor", null);
        var review = versions.ReviewDraft(edited.Version, Token);
        Assert.Equal(1, review.EffectiveRevision);
        var change = Assert.Single(review.Changes);
        Assert.Equal("Original", change.Before!.Value.GetProperty("name").GetString());
        Assert.Equal("Edited", change.After!.Value.GetProperty("name").GetString());
        devices.Write(edited.Device.Id, new(edited.Version, "meter", "Later", DeviceKind.Simulator), "editor", null);
        Assert.Equal("Edited", change.After.Value.GetProperty("name").GetString());
        Assert.Throws<DraftWriteException>(() => versions.ReviewDraft(edited.Version, Token));
        Assert.Single(versions.ReadHistory());
    }

    [Fact]
    public async Task EveryInvalidEnabledRouteIsListedAndDisabledRoutesDoNotBlockCompilation()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var first = routes.Write(null, new("0", "first", "First", true, InvalidGraph()), "operator", null);
        var second = routes.Write(null, new(first.Version, "second", "Second", true, InvalidGraph()), "operator", null);
        var disabled = routes.Write(null, new(second.Version, "disabled", "Disabled", false, InvalidGraph()), "operator", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var review = versions.ReviewDraft(disabled.Version, Token);
        Assert.False(review.PreflightPassed);
        Assert.Null(review.PlanHash);
        Assert.Equal(["first", "second"], review.RouteIssues.Select(issue => issue.RouteKey).Order());
        Assert.All(review.RouteIssues, issue => Assert.Equal("source.missing", issue.Code));
        Assert.Equal(3, review.Changes.Length);
        Assert.Empty(versions.ReadHistory());
    }

    [Fact]
    public async Task AllChangedTargetsWithPendingOrFailedRecordsAreListedAndQuarantineDoesNotBlock()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var plan = RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = TargetConnections.Select(id => new
            {
                kind = "output.http",
                key = "out" + id,
                configuration = new { connectionId = id, baseAddress = "http://localhost/", path = "events", inputs = Inputs },
            }),
        }));
        versions.Commit(versions.FreezeDraft(plan, "0", "operator"), null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var records = TargetConnections.Select(id => new DeliveryDraft(Guid.NewGuid(), id, Guid.NewGuid(), "device", 1,
            "{}"u8.ToArray(), "fp", "summary", "events", 1)).ToArray();
        var sequences = (await cache.EnqueueAsync(records, Token)).Seqs;
        await cache.RecordFailureAsync(new(sequences[1], 0, "rejected", true, false), Token);
        var review = versions.ReviewDraft("0", Token);
        Assert.False(review.PreflightPassed);
        Assert.Empty(review.RouteIssues);
        Assert.NotNull(review.PlanHash);
        Assert.Equal([1L, 2L], review.TargetBlocks.Select(block => block.ConnectionId));
        Assert.Contains("configuration.targetChangeRequiresDrainClearOrMigration", review.Warnings);
        Assert.True(review.TargetBlocks[0].HasPending);
        Assert.False(review.TargetBlocks[0].HasFailed);
        Assert.True(review.TargetBlocks[1].HasFailed);
        Assert.False(review.TargetBlocks[1].HasPending);
        Assert.NotNull(versions.TargetChangeProblem(Empty));
        await cache.QuarantineAsync(sequences[0], DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.Equal(2, Assert.Single(versions.ReviewDraft("0", Token).TargetBlocks).ConnectionId);
        Assert.Single(cache.ReadFailed(2, 0, 10));
        Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
        Assert.Single(versions.ReadHistory());
    }

    [Fact]
    public async Task LegacyBaselineIsExplicitlyUnavailableNotReportedAsAnEmptyConfiguration()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Empty, RuntimeSnapshotOrigins.Seed, null, "system"), null);
        var review = versions.ReviewDraft("0", Token);
        Assert.False(review.ComparisonAvailable);
        Assert.False(review.PreflightPassed);
        Assert.Contains("configuration.legacyComparisonUnavailable", review.Warnings);
        Assert.Empty(review.Changes);
        Assert.Single(versions.ReadHistory());
    }

    [Fact]
    public async Task CorruptBaselineIsNotConcealedAndReviewDoesNotAllocateARevision()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Empty, "0", "operator"), null);
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET content_hash = 'corrupt' WHERE revision = 1");
        Assert.Throws<RuntimePlanDocumentException>(() => versions.ReviewDraft("0", Token));
        Assert.Single(versions.ReadHistory());
        Assert.Null(versions.ReadPointers().PendingRevision);
    }

    [Fact]
    public async Task CancellationAndStaleVersionDoNotProduceAReviewOrChangeConfiguration()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => versions.ReviewDraft("0", cancellation.Token));
        Assert.Throws<DraftWriteException>(() => versions.ReviewDraft("stale", Token));
        Assert.Throws<ArgumentException>(() => versions.ReviewDraft(" ", Token));
        Assert.Empty(versions.ReadHistory());
    }

    [Fact]
    public void PointCountsAggregateDevicesAndIgnoreOrderingButDetectDetailsAndEnabledChanges()
    {
        var a = new ModbusPointSettings { Key = "a", FunctionCode = 3, Address = 0, Type = "uint16" };
        var b = a with { Key = "b", Address = 1 };
        var c = a with { Key = "c", Address = 2 };
        var first = new InputDraft(10, 1, "source", "Source", DeviceKind.Modbus, true, new(1000, [a, b]));
        var second = first with { Id = 11, Modbus = new(1000, [c]) };
        var before = new ConfigurationCheckpoint("before", [], [], [first, second], []);
        var reordered = before with { Inputs = [second, first with { Modbus = new(1000, [b, a]) }] };
        Assert.Empty(ConfigurationPointSummary.Between(before, reordered));
        var after = before with
        {
            Inputs = [first with { Modbus = new(1000, [a with { Enabled = false }, c]) },
                second with { Modbus = new(1000, [c with { Unit = "C" }]) },
                first with { Id = 20, DeviceId = 2, Modbus = new(1000, [a]) }]
        };
        var summaries = ConfigurationPointSummary.Between(before, after);
        Assert.Equal(new ConfigurationPointSummary(1, 1, 2, 1), summaries[0]);
        Assert.Equal(new ConfigurationPointSummary(2, 1, 0, 0), summaries[1]);
        Assert.Equal(2, summaries.Length);
    }

    private static RouteGraph InvalidGraph() => new(1,
        [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = MissingSourceIds }))], [], new([]));
}

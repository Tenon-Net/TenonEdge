using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class TargetChangeTests
{
    private static readonly string[] InputKeys = ["device/input"];
    [Theory]
    [InlineData("host", false)]
    [InlineData("port", false)]
    [InlineData("tls", false)]
    [InlineData("topic", false)]
    [InlineData("delete", false)]
    [InlineData("topic", true)]
    public async Task PendingOrFailedDataBlocksRecipientChangesAndCommitLeavesOldVersion(string change, bool failed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(before, null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var record = new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1, "{}"u8.ToArray(), "fp", "summary", "topic", 1);
        var seq = Assert.Single((await cache.EnqueueAsync([record], TestContext.Current.CancellationToken)).Seqs);
        if (failed)
        {
            await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), TestContext.Current.CancellationToken);
        }

        var next = Document(change);
        Assert.NotNull(versions.TargetChangeProblem(next));
        var revision = versions.Freeze(next, RuntimeSnapshotOrigins.Seed, null, "test");
        Assert.Throws<InvalidOperationException>(() => versions.Commit(revision, null));
        Assert.Equal(before, versions.ReadPointers().EffectiveRevision);
        Assert.Equal("pending", versions.Load(revision)!.State);
        Assert.Single(failed ? cache.ReadFailed(1, 0, 10) : cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task TimeoutChangeIsAllowedAndQuarantinedDataDoesNotBlockNewRecipient()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(before, null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var seq = Assert.Single((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1,
            "{}"u8.ToArray(), "fp", "summary", "topic", 1)], TestContext.Current.CancellationToken)).Seqs);
        Assert.Null(versions.TargetChangeProblem(Document("timeout")));
        await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, TestContext.Current.CancellationToken);
        Assert.Null(versions.TargetChangeProblem(Document("host")));
        var next = versions.Freeze(Document("host"), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(next, null);
        Assert.Equal(next, versions.ReadPointers().EffectiveRevision);
        Assert.Single(cache.ReadQuarantined(1, 0, 10));
    }

    private static RuntimePlanDocument Document(string? change = null)
    {
        var units = new List<object>();
        if (change != "delete")
        {
            units.Add(new
            {
                kind = "output.mqtt",
                key = "out",
                configuration = new
                {
                    connectionId = 1,
                    host = change == "host" ? "new.example" : "old.example",
                    port = change == "port" ? 1884 : 1883,
                    tls = change == "tls",
                    topic = change == "topic" ? "new-topic" : "topic",
                    inputs = InputKeys,
                    timeoutMs = change == "timeout" ? 6000 : 5000,
                },
            });
        }

        return RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units }));
    }
}

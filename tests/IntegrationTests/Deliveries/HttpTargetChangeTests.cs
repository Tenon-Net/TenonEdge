using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class HttpTargetChangeTests
{
    private static readonly string[] InputKeys = ["device/input"];
    [Theory]
    [InlineData("host", false)]
    [InlineData("port", false)]
    [InlineData("scheme", false)]
    [InlineData("path", false)]
    [InlineData("delete", false)]
    [InlineData("base", false)]
    [InlineData("query", false)]
    [InlineData("path", true)]
    public async Task PendingOrFailedDataBlocksRecipientChangesAndCommitLeavesOldVersion(string change, bool failed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(before, null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var record = new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1, "{}"u8.ToArray(), "fp", "summary", "path", 1);
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
            "{}"u8.ToArray(), "fp", "summary", "path", 1)], TestContext.Current.CancellationToken)).Seqs);
        Assert.Null(versions.TargetChangeProblem(Document("timeout")));
        await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, TestContext.Current.CancellationToken);
        Assert.Null(versions.TargetChangeProblem(Document("host")));
        var next = versions.Freeze(Document("host"), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(next, null);
        Assert.Equal(next, versions.ReadPointers().EffectiveRevision);
        Assert.Single(cache.ReadQuarantined(1, 0, 10));
    }

    [Fact]
    public async Task MultipleTopicsOnOneConnectionCanBeAddedButExistingRecipientsCannotDisappearWithBacklog()
    {
        RuntimePlanDocument Topics(params string[] topics) => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = topics.Select((topic, index) => new
            {
                kind = "output.mqtt",
                key = "path-" + index,
                configuration = new { connectionId = 1, host = "localhost", topic, inputs = InputKeys },
            }),
        }));
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.Freeze(Topics("a", "b"), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(before, null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var seq = Assert.Single((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1,
            "{}"u8.ToArray(), "fp", "summary", "a", 1)], TestContext.Current.CancellationToken)).Seqs);
        Assert.Null(versions.TargetChangeProblem(Topics("a", "b", "c")));
        Assert.Null(versions.TargetChangeProblem(Topics("b", "a")));
        Assert.NotNull(versions.TargetChangeProblem(Topics("a")));
        Assert.NotNull(versions.TargetChangeProblem(Topics("a", "replacement")));
        await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), TestContext.Current.CancellationToken);
        Assert.NotNull(versions.TargetChangeProblem(Topics("a")));
        var added = versions.Freeze(Topics("a", "b", "c"), RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(added, null);
        Assert.Equal(added, versions.ReadPointers().EffectiveRevision);
    }

    private static RuntimePlanDocument Document(string? change = null)
    {
        var units = new List<object>();
        if (change != "delete")
        {
            units.Add(new
            {
                kind = "output.http",
                key = "out",
                configuration = new
                {
                    connectionId = 1,
                    baseAddress = $"{(change == "scheme" ? "https" : "http")}://{(change == "host" ? "new.example" : "old.example")}:{(change == "port" ? 8081 : 8080)}/{(change == "base" ? "other/" : "api/")}",
                    path = change == "path" ? "new-path" : change == "query" ? "events?tenant=other" : "events",
                    inputs = InputKeys,
                    timeoutMs = change == "timeout" ? 6000 : 5000,
                },
            });
        }

        return RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units }));
    }
}

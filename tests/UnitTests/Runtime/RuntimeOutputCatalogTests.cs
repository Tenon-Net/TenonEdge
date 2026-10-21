using System.Text;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Runtime;

public sealed class RuntimeOutputCatalogTests
{
    [Fact]
    public void RepeatedFreshRowsOfTheSameVerifiedSnapshotReuseTheirCatalog()
    {
        var catalog = new RuntimeOutputCatalog();
        var first = catalog.Read(Snapshot());
        Assert.Equal("http", first[1].Kind);
        Assert.Single(first[1].Units);
        Assert.Same(first, catalog.Read(Snapshot()));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("hash")]
    [InlineData("schema")]
    [InlineData("oversized")]
    public void CorruptionAfterAWarmReadCannotReturnThePreviousCatalog(string damage)
    {
        var catalog = new RuntimeOutputCatalog();
        _ = catalog.Read(Snapshot());
        var changed = Snapshot();
        switch (damage)
        {
            case "content":
                changed.Content += " ";
                break;
            case "hash":
                changed.ContentHash = "sha256:" + new string('0', 64);
                break;
            case "schema":
                changed.SchemaVersion = 99;
                break;
            case "oversized":
                changed.Content = new string('x', (int)RuntimePlanDocument.MaximumBytes + 1);
                break;
            default:
                throw new InvalidOperationException();
        }
        Assert.Throws<RuntimePlanDocumentException>(() => catalog.Read(changed));
        Assert.Throws<RuntimePlanDocumentException>(() => catalog.Read(changed));
    }

    [Fact]
    public void RevisionAndVerifiedContentChangesReplaceRatherThanAccumulateEntries()
    {
        var catalog = new RuntimeOutputCatalog();
        var original = catalog.Read(Snapshot());
        var differentContent = catalog.Read(Snapshot(connectionId: 2));
        Assert.False(differentContent.ContainsKey(1));
        Assert.True(differentContent.ContainsKey(2));
        var nextRevision = Snapshot();
        nextRevision.Revision++;
        Assert.NotSame(original, catalog.Read(nextRevision));
        Assert.NotSame(original, catalog.Read(Snapshot()));
    }

    [Fact]
    public void IndependentHostsDoNotShareProjectionInstances()
    {
        Assert.NotSame(new RuntimeOutputCatalog().Read(Snapshot()), new RuntimeOutputCatalog().Read(Snapshot()));
    }

    [Fact]
    public async Task ConcurrentReadersShareOnlyTheSameVerifiedImmutableProjection()
    {
        var catalog = new RuntimeOutputCatalog();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => catalog.Read(Snapshot()), TestContext.Current.CancellationToken)));
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public void CompleteSnapshotsUseFrozenNamesAndLegacyQueueOnlyExecutionTargets()
    {
        var empty = RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);
        var connection = new ConnectionDraft(1, "http1", "Frozen receiver", ConnectionKind.Http, true, null, null,
            new("http://server.test/api/"));
        var frozen = RuntimeSnapshotDocument.Create(empty, new("frozen", [connection], [], [], [], []));
        var snapshot = new RuntimeSnapshotRecord
        {
            Revision = 7,
            SchemaVersion = frozen.SchemaVersion,
            Content = frozen.Content,
            ContentHash = frozen.ContentHash,
        };
        var catalog = new RuntimeOutputCatalog();
        var result = catalog.Read(snapshot);
        Assert.Equal("http1", result[1].Key);
        Assert.Equal("Frozen receiver", result[1].Name);
        Assert.Equal("queue/http1/sender", Assert.Single(result[1].Units).Key);
        Assert.Same(result, catalog.Read(snapshot));
        Assert.Equal(frozen.Content, snapshot.Content);
        Assert.Equal(frozen.ContentHash, snapshot.ContentHash);
    }

    private static RuntimeSnapshotRecord Snapshot(long connectionId = 1)
    {
        var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes($$$"""
            {"schemaVersion":1,"units":[
              {"kind":"input.simulator","key":"device/sample","configuration":{"periodMs":1000}},
              {"kind":"output.http","key":"gateway/http","configuration":{
                "connectionId":{{{connectionId}}},"baseAddress":"http://127.0.0.1:12345/","path":"capture","inputs":["device/sample"]}}
            ]}
            """));
        return new() { Revision = 7, SchemaVersion = 1, Content = plan.CanonicalText, ContentHash = plan.ContentHash };
    }
}

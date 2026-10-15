using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.UnitTests.Routes;

public sealed class RouteTestSendArtifactsTests
{
    [Fact]
    public void CapturesCompleteDetachedBytesRatherThanTheTruncatedDisplayAndNeverPrintsBody()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 200000));
        var capture = Capture(bytes);
        var item = Assert.Single(capture.Items);
        bytes[0] = (byte)'z';
        Assert.Equal((byte)'x', item.Payload.Span[0]);
        Assert.Equal(200000, item.Payload.Length);
        Assert.Equal("", item.Output.Body);
        Assert.True(item.Output.Truncated);
        Assert.DoesNotContain("display-only", item.ToString(), StringComparison.Ordinal);
        Assert.True(item.BudgetBytes > item.Payload.Length);
    }

    [Fact]
    public void OwnershipPreparationReplacementAndOneConsumptionAreAtomic()
    {
        using var cache = new RouteTestSendArtifacts(new FakeTimeProvider());
        var capture = Capture();
        var item = capture.Items[0];
        Assert.True(cache.Publish("a", capture));
        Assert.Equal(404, Assert.Throws<RouteTestSendException>(() => cache.Read(item.Id, "b")).Status);
        var first = Prepare(cache, item, "a");
        var second = Prepare(cache, item, "a");
        Assert.Throws<RouteTestSendException>(() => cache.Consume(item.Id, first.ConfirmationId, "a", true));
        Assert.Throws<RouteTestSendException>(() => cache.Consume(item.Id, second.ConfirmationId, "b", true));
        Assert.Throws<ArgumentException>(() => cache.Consume(item.Id, second.ConfirmationId, "a", false));
        Assert.Same(item, cache.Consume(item.Id, second.ConfirmationId, "a", true).Artifact);
        Assert.Throws<RouteTestSendException>(() => cache.Consume(item.Id, second.ConfirmationId, "a", true));
    }

    [Fact]
    public async Task ConcurrentConfirmationsYieldExactlyOneOwner()
    {
        using var cache = new RouteTestSendArtifacts(new FakeTimeProvider());
        var capture = Capture();
        cache.Publish("owner", capture);
        var item = capture.Items[0];
        var review = Prepare(cache, item, "owner");
        var attempts = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try
            { cache.Consume(item.Id, review.ConfirmationId, "owner", true); return true; }
            catch (RouteTestSendException) { return false; }
        }, TestContext.Current.CancellationToken));
        Assert.Single(await Task.WhenAll(attempts), value => value);
    }

    [Fact]
    public void LatestPreviewReplacesOnlyItsOwnSessionAndNewInstanceHasNoOldArtifacts()
    {
        using var cache = new RouteTestSendArtifacts(new FakeTimeProvider());
        var a = Capture();
        var b = Capture();
        var next = Capture();
        cache.Publish("a", a);
        cache.Publish("b", b);
        cache.Publish("a", next);
        Assert.Throws<RouteTestSendException>(() => cache.Read(a.Items[0].Id, "a"));
        Assert.Same(b.Items[0], cache.Read(b.Items[0].Id, "b"));
        Assert.Same(next.Items[0], cache.Read(next.Items[0].Id, "a"));
        using var restarted = new RouteTestSendArtifacts(new FakeTimeProvider());
        Assert.Throws<RouteTestSendException>(() => restarted.Read(next.Items[0].Id, "a"));
    }

    [Fact]
    public void PreparationDoesNotExtendLifetimeAndExpiryClearsCapacity()
    {
        var time = new FakeTimeProvider();
        using var cache = new RouteTestSendArtifacts(time);
        var capture = Capture();
        cache.Publish("owner", capture);
        time.Advance(RouteTestSendArtifacts.Lifetime - TimeSpan.FromSeconds(1));
        var review = Prepare(cache, capture.Items[0], "owner");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<RouteTestSendException>(() => cache.Consume(review.Id, review.ConfirmationId, "owner", true));
        Assert.True(cache.Publish("owner", Capture()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256 * 1024)]
    public void GlobalCountAndByteLimitsRefuseWithoutEvictingOtherSessions(int payloadBytes)
    {
        using var cache = new RouteTestSendArtifacts(new FakeTimeProvider());
        var first = Capture(new byte[payloadBytes]);
        Assert.True(cache.Publish("first", first));
        var admitted = 1;
        var bytes = first.Items[0].BudgetBytes;
        for (var index = 0; index < 300; index++)
        {
            var capture = Capture(new byte[payloadBytes]);
            if (!cache.Publish("owner-" + index, capture))
            { break; }
            admitted++;
            bytes += capture.Items[0].BudgetBytes;
        }
        Assert.InRange(admitted, 1, RouteTestSendArtifacts.MaximumCount);
        Assert.InRange(bytes, 1, RouteTestSendArtifacts.MaximumBytes);
        Assert.Same(first.Items[0], cache.Read(first.Items[0].Id, "first"));
        Assert.False(cache.Publish("cannot-fit", Capture(new byte[payloadBytes])));
    }

    [Fact]
    public void PerPreviewLimitsAndUnsupportedProtocolsNeverProduceUnboundedArtifacts()
    {
        var capture = new RouteTestSendCapture();
        var added = 0;
        while (Add(capture, new byte[256 * 1024]))
        { added++; }
        Assert.InRange(added, 1, RouteTestSendCapture.MaximumCount);
        Assert.InRange(capture.Items.Sum(item => item.BudgetBytes), 1, RouteTestSendCapture.MaximumBytes);
        Assert.False(Add(capture, [1], "output.tenoniot"));
        Assert.False(Add(capture, new byte[256 * 1024 + 1]));
    }

    private static RouteTestSendCapture Capture(byte[]? bytes = null)
    {
        var capture = new RouteTestSendCapture();
        Assert.True(Add(capture, bytes ?? "{\"value\":9223372036854775807}"u8.ToArray()));
        return capture;
    }
    private static bool Add(RouteTestSendCapture capture, byte[] bytes, string kind = "output.http")
    {
        var id = Guid.NewGuid();
        return capture.TryAdd(id, "version", 1, Guid.NewGuid(), DateTimeOffset.UtcNow, ["in", "out"],
            JsonSerializer.SerializeToElement(new { connectionId = 1 }),
            new("out", kind, Guid.NewGuid(), "events", null, ImmutableDictionary<string, string>.Empty, "display-only", bytes.Length, true, id), bytes);
    }
    private static RouteTestSendReview Prepare(RouteTestSendArtifacts cache, RouteTestSendArtifact item, string owner) =>
        cache.Prepare(item.Id, owner, cache.Read(item.Id, owner), 1, "http://localhost/", "none", null, null, null);
}

using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.UnitTests.Runtime;

public sealed class DeliveryRetryRequestTests
{
    [Fact]
    public void CapturesSelectedGenerationsAndTargetsWithoutRetainingCallerOwnedCollections()
    {
        var first = new DeliveryRetryItem(1, Guid.NewGuid());
        var second = new DeliveryRetryItem(2, Guid.NewGuid());
        DeliveryRetryItem[] items = [second, first];
        var targets = new Dictionary<long, string> { [1] = "original" };
        var request = new RetryFailedRequest(items, targets);
        items[0] = new(3, Guid.NewGuid());
        targets[1] = "changed";
        Assert.Equal(new[] { first, second }, request.Items);
        Assert.Equal("original", request.TargetFingerprints[1]);
    }

    [Fact]
    public void RejectsUnboundedEmptyDuplicateOrInvalidSelections()
    {
        var item = new DeliveryRetryItem(1, Guid.NewGuid());
        var targets = new Dictionary<long, string> { [1] = "fp" };
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([], targets));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([item, item], targets));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([item, new(2, item.DeliveryId)], targets));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([item, new(1, Guid.NewGuid())], targets));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([new(0, Guid.NewGuid())], targets));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([new(1, Guid.Empty)], targets));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([item], new Dictionary<long, string>()));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([item], new Dictionary<long, string> { [0] = "fp" }));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest([item], new Dictionary<long, string> { [1] = "" }));
        Assert.Throws<ArgumentException>(() => new RetryFailedRequest(
            Enumerable.Range(1, RetryFailedRequest.MaximumItems + 1).Select(i => new DeliveryRetryItem(i, Guid.NewGuid())).ToArray(), targets));
    }
}

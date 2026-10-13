using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.UnitTests.Runtime;

public sealed class DeliveryCleanupSelectionTests
{
    [Fact]
    public void FreezesSortedSelectionWithoutRetainingMutableCallerCollections()
    {
        var first = new DeliveryCleanupItem(1, Guid.NewGuid(), 11, null);
        var second = new DeliveryCleanupItem(2, Guid.NewGuid(), 12, long.MaxValue);
        DeliveryCleanupItem[] items = [second, first];
        var selection = new DeliveryCleanupSelection(DeliveryState.Quarantined, items);
        items[0] = new(3, Guid.NewGuid(), 11, null);
        Assert.Equal(new[] { first, second }, selection.Items);
        Assert.Equal(DeliveryState.Quarantined, selection.State);
        Assert.Throws<NotSupportedException>(() => ((IList<DeliveryCleanupItem>)selection.Items)[0] = second);
    }

    [Fact]
    public void RefusesPendingUndefinedEmptyUnboundedDuplicateAndInvalidIdentities()
    {
        var item = new DeliveryCleanupItem(1, Guid.NewGuid(), 11, null);
        Assert.Throws<ArgumentException>(() => new DeliveryCleanupSelection(DeliveryState.Pending, [item]));
        Assert.Throws<ArgumentException>(() => new DeliveryCleanupSelection((DeliveryState)999, [item]));
        foreach (var items in new DeliveryCleanupItem[][] {
            [], [item, item], [item, item with { Sequence = 2 }], [item, item with { DeliveryId = Guid.NewGuid() }],
            [item with { Sequence = 0 }], [item with { DeliveryId = Guid.Empty }], [item with { ConnectionId = 0 }],
            [item with { ConnectionId = 9007199254740992L }], [item with { MigrationId = 0 }], [null!],
            Enumerable.Range(1, 1001).Select(i => new DeliveryCleanupItem(i, Guid.NewGuid(), 11, null)).ToArray(),
        })
        { Assert.Throws<ArgumentException>(() => new DeliveryCleanupSelection(DeliveryState.Failed, items)); }
        Assert.Equal(1000, new DeliveryCleanupSelection(DeliveryState.Failed,
            Enumerable.Range(1, 1000).Select(i => new DeliveryCleanupItem(i, Guid.NewGuid(), 11, null)).ToArray()).Items.Count);
    }
}

using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.UnitTests.Runtime;

public sealed class DeliveryMigrationCommandTests
{
    [Fact]
    public void SelectionIsCopiedSortedAndItsHashIsOrderIndependent()
    {
        var operation = Guid.NewGuid();
        DeliveryMigrationItem[] items = [new(2, Guid.NewGuid(), "old", 4), new(1, Guid.NewGuid(), "old", null)];
        var command = DeliveryMigrationCommand.Selected("store", operation, 1, 2, "new", "summary", items);
        var reverse = DeliveryMigrationCommand.Selected("store", operation, 1, 2, "new", "summary", items.Reverse().ToArray());
        Assert.Equal(command.RequestHash, reverse.RequestHash);
        Assert.Equal(1, command.Items![0].Sequence);
        items[1] = items[1] with { Fingerprint = "changed" };
        Assert.Equal("old", command.Items[0].Fingerprint);
        Assert.NotEqual(command.RequestHash, DeliveryMigrationCommand.Selected("store", operation, 1, 2, "new", "summary", items).RequestHash);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("operation")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("fingerprint")]
    [InlineData("summary")]
    [InlineData("generation")]
    [InlineData("selection")]
    public void HashBindsEveryMigrationDecision(string change)
    {
        var operation = Guid.NewGuid();
        DeliveryMigrationItem[] items = [new(1, Guid.NewGuid(), "old", null)];
        var original = DeliveryMigrationCommand.Selected("store", operation, 1, 2, "new", "summary", items);
        var changed = DeliveryMigrationCommand.Selected(change == "store" ? "other" : "store", change == "operation" ? Guid.NewGuid() : operation,
            change == "source" ? 3 : 1, change == "target" ? 3 : 2, change == "fingerprint" ? "other" : "new", change == "summary" ? "other" : "summary",
            change == "generation" ? [items[0] with { MigrationId = 1 }] : change == "selection" ? [items[0] with { Sequence = 2 }] : items);
        Assert.NotEqual(original.RequestHash, changed.RequestHash);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicateSequence")]
    [InlineData("duplicateId")]
    [InlineData("emptyId")]
    [InlineData("zeroSequence")]
    [InlineData("zeroGeneration")]
    [InlineData("emptyFingerprint")]
    public void InvalidSelectionsAreRejectedBeforeEnteringWriter(string invalid)
    {
        DeliveryMigrationItem item = new(1, Guid.NewGuid(), "old", null);
        DeliveryMigrationItem[] items = invalid switch
        {
            "empty" => [],
            "duplicateSequence" => [item, item with { DeliveryId = Guid.NewGuid() }],
            "duplicateId" => [item, item with { Sequence = 2 }],
            "emptyId" => [item with { DeliveryId = Guid.Empty }],
            "zeroSequence" => [item with { Sequence = 0 }],
            "zeroGeneration" => [item with { MigrationId = 0 }],
            _ => [item with { Fingerprint = " " }],
        };
        Assert.Throws<ArgumentException>(() => DeliveryMigrationCommand.Selected("store", Guid.NewGuid(), 1, 2, "new", "summary", items));
    }

    [Fact]
    public void OldTargetRangeHasNoPaginationAndIsBoundToOneConnectionAndOneOldFingerprint()
    {
        var operation = Guid.NewGuid();
        var command = DeliveryMigrationCommand.OldTarget("store", operation, 1, "old", "new", "summary");
        Assert.Equal(command.SourceConnectionId, command.TargetConnectionId);
        Assert.Null(command.Items);
        Assert.Equal("old", command.OldFingerprint);
        Assert.NotEqual(command.RequestHash, DeliveryMigrationCommand.OldTarget("store", operation, 1, "other", "new", "summary").RequestHash);
        Assert.Throws<ArgumentException>(() => DeliveryMigrationCommand.OldTarget("store", operation, 1, " ", "new", "summary"));
        Assert.Throws<ArgumentException>(() => DeliveryMigrationCommand.OldTarget("store", Guid.Empty, 1, "old", "new", "summary"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryMigrationCommand.OldTarget("store", operation, 0, "old", "new", "summary"));
    }
}

using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.UnitTests.Runtime;

public sealed class DeliveryMigrationCommandTests
{
    [Theory]
    [InlineData("store")]
    [InlineData("intent")]
    [InlineData("connection")]
    [InlineData("largeConnection")]
    [InlineData("fingerprint")]
    [InlineData("longFingerprint")]
    [InlineData("utf8Fingerprint")]
    public void InvalidHoldIdentityIsRejectedBeforeEnteringWriter(string invalid)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DeliveryMigrationHold(
            invalid == "store" ? "not-a-store-id" : "00000000-0000-4000-8000-000000000001",
            invalid == "intent" ? Guid.Empty : Guid.NewGuid(),
            invalid == "connection" ? 0 : invalid == "largeConnection" ? 9007199254740992L : 1,
            invalid switch { "fingerprint" => " ", "longFingerprint" => new string('x', 1025), "utf8Fingerprint" => new string('中', 512), _ => "old" }));
    }

    [Fact]
    public void IntentAndAttemptHashesBindTheirDifferentIdentities()
    {
        var hold = new DeliveryMigrationHold("00000000-0000-4000-8000-000000000001", Guid.NewGuid(), 1, "old");
        var same = new DeliveryMigrationHold(hold.ConfigStoreId, hold.IntentId, hold.ConnectionId, hold.OldFingerprint);
        Assert.Equal(hold.RequestHash, same.RequestHash);
        var other = new DeliveryMigrationHold(hold.ConfigStoreId, Guid.NewGuid(), 1, "old");
        Assert.NotEqual(hold.RequestHash, other.RequestHash);
        var operation = Guid.NewGuid();
        var command = DeliveryMigrationCommand.ForIntent(hold, operation, "new", "summary");
        Assert.Same(hold, command.Hold);
        Assert.Null(command.Items);
        Assert.Equal(hold.ConnectionId, command.SourceConnectionId);
        Assert.Equal(command.SourceConnectionId, command.TargetConnectionId);
        Assert.Equal(command.RequestHash, DeliveryMigrationCommand.ForIntent(same, operation, "new", "summary").RequestHash);
        Assert.NotEqual(command.RequestHash, DeliveryMigrationCommand.ForIntent(other, operation, "new", "summary").RequestHash);
        Assert.NotEqual(command.RequestHash, DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "summary").RequestHash);
        Assert.NotEqual(command.RequestHash, DeliveryMigrationCommand.OldTarget(hold.ConfigStoreId, operation, 1, "old", "new", "summary").RequestHash);
    }

    [Fact]
    public void ExistingVersionOneCommandHashIsNotChangedByAddingIntentSupport()
    {
        // Fixed v1 canonical bytes; preserving existing durable receipts is not tested only against the new serializer itself.
        var command = DeliveryMigrationCommand.OldTarget("store", Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, "old", "new", "summary");
        Assert.Equal("F83608396DDD626CCB229D1BB98CEBD626EAB0434589E9CACAE3F1FDAD670D74", command.RequestHash);
    }

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

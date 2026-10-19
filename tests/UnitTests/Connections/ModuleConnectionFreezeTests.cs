using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Parameters;

namespace TenonEdge.UnitTests.Connections;

public sealed class ModuleConnectionFreezeTests
{
    private static ConnectionDraft Parent(long id, Dictionary<string, long> references) => new(id, "p" + id, "Parent", ConnectionKind.Module,
        true, null, null, null, new("wrapper", 1, JsonSerializer.SerializeToElement(new { value = 9007199254740993L }), references));
    private static ConnectionDraft Leaf(long id) => new(id, "leaf", "Leaf", ConnectionKind.Http, true, null, null, new("http://localhost/"));

    [Fact]
    public void FreezingUsesCandidateDescendantsAndDoesNotMutateTheDraft()
    {
        var parent = Parent(1, new() { ["upstream"] = 2 });
        var child = Parent(2, new() { ["upstream"] = 3 });
        var available = new[] { parent, child, Leaf(3) }.ToDictionary(value => value.Id);
        var frozen = parent.Module!.Snapshot(1, available);
        var ids = ModuleConnectionReferences.Flatten([frozen]).Select(value => value.ConnectionId);
        Assert.Equal([1L, 2L, 3L], ids);
        Assert.False(parent.Module.Parameters.TryGetProperty("upstream", out _));
        Assert.Equal("9007199254740993", ModuleConnectionSnapshot.ReadModuleConfiguration(frozen).Parameters.GetProperty("value").GetRawText());
        available[3] = available[3] with { Http = new("http://changed/") };
        Assert.Contains("http://localhost/", frozen.Configuration.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("http://changed/", frozen.Configuration.GetRawText(), StringComparison.Ordinal);
        Assert.NotEqual(ModuleConnectionSnapshot.ModuleConfigurationIdentity(frozen),
            ModuleConnectionSnapshot.ModuleConfigurationIdentity(parent.Module.Snapshot(1, available)));
    }

    [Fact]
    public void MissingDisabledCyclesAndDuplicateScalarReferencesAreRejected()
    {
        var parent = Parent(1, new() { ["upstream"] = 2 });
        Assert.Throws<ArgumentException>(() => parent.Module!.Snapshot(1));
        var available = new[] { parent, Leaf(2) with { Enabled = false } }.ToDictionary(value => value.Id);
        _ = parent.Module!.Snapshot(1, available); // Disabled drafts may still be edited/exported.
        Assert.Throws<ArgumentException>(() => parent.Module.Snapshot(1, available, requireEnabled: true));
        available[2] = Parent(2, new() { ["back"] = 1 });
        Assert.Throws<ArgumentException>(() => parent.Module.Snapshot(1, available));
        Assert.Throws<ArgumentException>(() => (parent.Module with { Connections = new() { ["value"] = 2 } }).Normalize(1));
    }

    [Fact]
    public void SharedDagIsRejectedBeforeExponentialJsonMaterialization()
    {
        var available = new Dictionary<long, ConnectionDraft> { [12] = Leaf(12) };
        for (var id = 11; id > 0; id--)
        { available[id] = Parent(id, new() { ["left"] = id + 1, ["right"] = id + 1 }); }
        Assert.Contains("大小限制", Assert.Throws<ArgumentException>(() => available[1].Module!.Snapshot(1, available)).Message, StringComparison.Ordinal);
        Assert.Equal(12, available.Count);
        // A shallow diamond remains legal and shares the same descendant identity.
        var small = available[10].Module!.Snapshot(10, available);
        Assert.Equal([10L, 11L, 12L], ModuleConnectionReferences.Flatten([small]).Select(value => value.ConnectionId));
    }

    [Fact]
    public void ScalarBudgetDoesNotShrinkByAddingAnArtificialRootReferenceEnvelope()
    {
        var parameters = JsonSerializer.SerializeToElement(new { text = new string('x', ParameterSchema.MaximumConfigurationBytes - 11) });
        Assert.Equal(ParameterSchema.MaximumConfigurationBytes, System.Text.Encoding.UTF8.GetByteCount(parameters.GetRawText()));
        var draft = new ModuleConnectionDraft("wrapper", 1, parameters);
        Assert.Equal(parameters.GetRawText(), ModuleConnectionSnapshot.ReadModuleConfiguration(draft.Snapshot(1)).Parameters.GetRawText());
    }

    [Fact]
    public void DirectReferenceMapsStillHaveTheirOwnAggregateByteLimit()
    {
        var parameters = JsonSerializer.SerializeToElement(new { text = new string('x', 65500) });
        var available = new[] { Parent(1, []), Parent(2, []) }.Select(value => value with { Module = value.Module! with { Parameters = parameters } })
            .ToDictionary(value => value.Id);
        Assert.Throws<ArgumentException>(() => ModuleDraftConnections.Freeze(new Dictionary<string, long> { ["one"] = 1, ["two"] = 2 }, available, null));
    }
}

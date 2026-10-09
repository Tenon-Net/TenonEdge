using System.Text.Json;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.UnitTests.Routes;

public sealed class RouteGraphFormatTests
{
    private static RouteGraph Graph(string type = "input.simulator", string config = "{\"sourceIds\":[]}") => new(1,
        [new("n", type, 1, "unfinished", JsonSerializer.Deserialize<JsonElement>(config))], [], new(new() { ["n"] = new(12.5, -24) }));

    [Fact]
    public void IncompleteAndCyclicDraftsAreSavableButNoExecutionStateIsStored()
    {
        var graph = Graph() with { Edges = [new("n", "n"), new("n", "missing")] };
        var saved = RouteGraphFormat.Normalize(graph);
        Assert.Equal(2, saved.Edges.Length);
        Assert.Empty(saved.Nodes[0].Config.GetProperty("sourceIds").EnumerateArray());
        Assert.Equal(new RouteNodePosition(12.5, -24), saved.Layout.Nodes["n"]);
        graph.Layout.Nodes.Clear();
        Assert.Single(saved.Layout.Nodes);
    }

    [Fact]
    public void ReferencesAreDistinctAndIncludeDisabledOrMissingEntitiesWithoutPretendingTheyExist()
    {
        var graph = Graph(config: "{\"sourceIds\":[9,9,10]}");
        var normalized = RouteGraphFormat.Normalize(graph with
        {
            Nodes = [.. graph.Nodes,
            new("out", "output.http", 1, "out", JsonSerializer.Deserialize<JsonElement>("{\"connectionId\":7}"))]
        });
        Assert.Equal([new RouteGraphReference("input", 9), new("input", 10), new("connection", 7)], RouteGraphFormat.References(normalized));
    }

    [Theory]
    [InlineData("input.simulator", "{\"sourceIds\":[],\"password\":\"never-store\"}")]
    [InlineData("transform.map", "{\"rules\":[{\"script\":\"never-run\"}]}")]
    [InlineData("output.mqtt", "{\"host\":\"override.example\"}")]
    [InlineData("output.http", "{\"headers\":{\"Authorization\":\"secret\"}}")]
    [InlineData("output.http", "{\"payload\":{\"mode\":\"custom\",\"fields\":[{\"script\":\"no\"}]}}")]
    [InlineData("output.http", "{\"headers\":{\"Cookie\":\"secret\"}}")]
    [InlineData("output.http", "{\"headers\":{\"proxy-authorization\":\"secret\"}}")]
    [InlineData("input.script", "{}")]
    [InlineData("input.simulator", "{\"sourceIds\":[0]}")]
    [InlineData("output.tenon", "{\"connectionId\":9007199254740992}")]
    public void UnknownOrSecretConfigurationAndUnsafeIdsAreRejected(string type, string config)
    {
        Assert.True(Record.Exception(() => RouteGraphFormat.Normalize(Graph(type, config))) is ArgumentException or JsonException);
    }

    [Theory]
    [InlineData("graphVersion")]
    [InlineData("nodeVersion")]
    [InlineData("duplicateId")]
    [InlineData("nodeLimit")]
    [InlineData("coordinate")]
    [InlineData("orphanLayout")]
    public void StructuralAndLayoutLimitsCannotBeBypassedBySavingAsADraft(string scenario)
    {
        var graph = Graph();
        graph = scenario switch
        {
            "graphVersion" => graph with { SchemaVersion = 2 },
            "nodeVersion" => graph with { Nodes = [graph.Nodes[0] with { Version = 2 }] },
            "duplicateId" => graph with { Nodes = [graph.Nodes[0], graph.Nodes[0]] },
            "nodeLimit" => graph with { Nodes = Enumerable.Range(0, 51).Select(i => graph.Nodes[0] with { Id = $"n{i}" }).ToArray() },
            "coordinate" => graph with { Layout = new(new() { ["n"] = new(double.NaN, 0) }) },
            _ => graph with { Layout = new(new() { ["absent"] = new(1, 2) }) },
        };
        Assert.Throws<ArgumentException>(() => RouteGraphFormat.Normalize(graph));
    }

    [Fact]
    public void TotalDocumentLimitAppliesEvenWhenEachRuleIsIndividuallyBounded()
    {
        var config = JsonSerializer.Serialize(new { rules = Enumerable.Range(0, 17).Select(i => new { target = $"v{i}", type = "string", constant = new string('x', 65000) }) });
        Assert.Throws<ArgumentException>(() => RouteGraphFormat.Normalize(Graph("transform.map", config)));
    }

    [Fact]
    public void RuleConstantsPreserveExactInt64AndUnfinishedRulesDoNotNeedToCompileYet()
    {
        var saved = RouteGraphFormat.Normalize(Graph("transform.map", """{"rules":[{}, {"target":"exact","type":"integer","constant":9223372036854775807}]}"""));
        Assert.Equal("9223372036854775807", saved.Nodes[0].Config.GetProperty("rules")[1].GetProperty("constant").GetRawText());
        Assert.False(saved.Nodes[0].Config.GetProperty("rules")[0].TryGetProperty("constant", out _));
    }
}

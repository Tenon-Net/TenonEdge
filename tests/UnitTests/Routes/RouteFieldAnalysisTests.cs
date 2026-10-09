using System.Text.Json;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.UnitTests.Routes;

public sealed class RouteFieldAnalysisTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RouteGraphNode Node(string id, string type, string config) => new(id, type, 1, id, JsonSerializer.Deserialize<JsonElement>(config));
    private static RouteGraphNode Input(string id = "in", string sources = "[1]") => Node(id, "input.simulator", "{\"sourceIds\":" + sources + "}");
    private static RouteGraphNode Map(string id, string from, string to) => Node(id, "transform.map", $$"""{"rules":[{"target":"{{to}}","type":"integer","sourceField":"{{from}}"}]}""");
    private static RouteGraph Graph(RouteGraphNode[] nodes, params RouteGraphEdge[] edges) => new(1, nodes, edges, new([]));
    private static RouteFieldPage Analyze(RouteGraph graph, string selected, string? after = null, string? search = null, int limit = 100, string sample = "{\"value\":42}")
    {
        var prefix = RouteFieldAnalysis.Prefix(graph, selected);
        var snapshot = new RouteCompilationSnapshot("version", [new(0, "field-analysis", "Fields", null, true, prefix, null, 0)],
            new Dictionary<long, DeviceDraft> { [1] = new(1, "sim", "Sim", DeviceKind.Simulator, null, null, null, ModbusAddressNotation.Offset, true) },
            new Dictionary<long, InputDraft> { [1] = new(1, 1, "values", "Values", DeviceKind.Simulator, true, null, Simulator: new(SampleJson: sample)) },
            new Dictionary<long, TenonEdge.Host.Features.Connections.ConnectionDraft>(), new HashSet<long>());
        return RouteFieldAnalysis.Analyze(snapshot, selected, after, search, limit, Token);
    }

    [Fact]
    public void SelectedMappingNeedsOnlyItsAncestorsAndNotItsOwnIncompleteRulesOrOutputs()
    {
        var graph = Graph([Input(), Map("first", "value", "renamed"), Node("selected", "transform.map", "{\"rules\":[]}"), Node("out", "output.http", "{\"connectionId\":null}")],
            new("in", "first"), new("first", "selected"), new("selected", "out"));
        var result = Analyze(graph, "selected");
        Assert.True(result.Analyzed);
        Assert.Equal("renamed", Assert.Single(result.Fields).Key);
        Assert.Equal("selected", result.NodeId);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void FanInOffersOnlySharedKeyTypesAndReportsExcludedFields()
    {
        var graph = Graph([Input(), Map("a", "value", "a"), Map("b", "value", "b"), Node("out", "output.http", "{}")],
            new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out"));
        var result = Analyze(graph, "out");
        Assert.Empty(result.Fields);
        Assert.Equal(2, result.UpstreamCount);
        Assert.Equal(["a", "b"], result.ExcludedFields);
        Assert.False(result.DifferencesTruncated);
    }

    [Fact]
    public void InputNodesCanDescribeTheirOwnSourcesAndFieldsAreFilteredAndPaged()
    {
        var graph = Graph([Input("field-sink")]);
        var first = Analyze(graph, "field-sink", limit: 1, sample: "{\"b\":2,\"a\":1,\"c\":3}");
        Assert.Equal("a", Assert.Single(first.Fields).Key);
        Assert.Equal("a", first.NextAfterKey);
        var second = Analyze(graph, "field-sink", first.NextAfterKey, limit: 1, sample: "{\"b\":2,\"a\":1,\"c\":3}");
        Assert.Equal("b", Assert.Single(second.Fields).Key);
        Assert.Equal("c", Assert.Single(Analyze(graph, "field-sink", search: "C", sample: "{\"b\":2,\"a\":1,\"c\":3}").Fields).Key);
    }

    [Fact]
    public void InvalidUpstreamMappingsAndCyclesAreNotHiddenByPrefixTrimming()
    {
        var graph = Graph([Input(), Map("a", "absent", "a"), Map("selected", "a", "b")], new("in", "a"), new("a", "selected"));
        Assert.Equal("field.missing", Assert.Throws<RouteTopologyException>(() => Analyze(graph, "selected")).Issue.Code);
        var cyclic = graph with { Edges = [.. graph.Edges, new("selected", "a")] };
        Assert.Equal("graph.cycle", Assert.Throws<RouteTopologyException>(() => Analyze(cyclic, "selected")).Issue.Code);
        Assert.Equal("source.missing", Assert.Throws<RouteTopologyException>(() => Analyze(Graph([Input(sources: "[99]")]), "in")).Issue.Code);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FanInDistinguishesTypeAndUnitDifferences(bool differentType)
    {
        var a = Node("a", "transform.map", """{"rules":[{"target":"value","type":"integer","sourceField":"value","unit":"W"}]}""");
        var b = Node("b", "transform.map", differentType
            ? """{"rules":[{"target":"value","type":"number","sourceField":"value","unit":"W"}]}"""
            : """{"rules":[{"target":"value","type":"integer","sourceField":"value","unit":"kW"}]}""");
        var graph = Graph([Input(), a, b, Node("out", "output.http", "{}")], new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out"));
        var result = Analyze(graph, "out");
        if (differentType)
        { Assert.Empty(result.Fields); Assert.Equal("value", Assert.Single(result.ExcludedFields)); }
        else
        { Assert.Null(Assert.Single(result.Fields).Unit); Assert.True(result.UnitsDiffer); }
    }

    [Fact]
    public void DifferenceDiagnosticsAreBoundedEvenWhenBranchesHaveDisjointLargeSchemas()
    {
        RouteGraphNode Branch(string id) => Node(id, "transform.map", JsonSerializer.Serialize(new
        { rules = Enumerable.Range(0, 1000).Select(i => new { target = id + i, type = "integer", constant = 1 }) }));
        var graph = Graph([Input(), Branch("a"), Branch("b"), Node("out", "output.http", "{}")],
            new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out"));
        var result = Analyze(graph, "out");
        Assert.Empty(result.Fields);
        Assert.Equal(100, result.ExcludedFields.Length);
        Assert.True(result.DifferencesTruncated);
    }

}

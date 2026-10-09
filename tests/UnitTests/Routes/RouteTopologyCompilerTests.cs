using System.Collections.Immutable;
using System.Diagnostics;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.UnitTests.Routes;

public sealed class RouteTopologyCompilerTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RouteTopologyNode Input(string id = "in", params long[] sources) => new(id, "input.simulator", 1, (sources.Length == 0 ? [1L] : sources).ToImmutableArray());
    private static RouteTopologyNode Map(string id) => new(id, "transform.map", 1, []);
    private static RouteTopologyNode Out(string id = "out") => new(id, "output.mqtt", 1, []);
    private static RouteTopologySource Source(long id) => new(id, RuntimeUnitId.Create("input.simulator", $"device{id}/values"), true,
        [new("value", FieldType.Number, "V")]);
    private static Dictionary<long, RouteTopologySource> Sources(int count = 1) => Enumerable.Range(1, count).ToDictionary(index => (long)index, index => Source(index));
    private static RouteTopology Straight(string key = "route", params long[] sources) => new(key, true, 1, [Input("in", sources), Out()], [new("in", "out")]);
    private static RouteTopologyResult Compile(params RouteTopology[] routes) => RouteTopologyCompiler.Compile(routes, Sources(), Token);
    private static string Path(RouteTopologyPath path) => $"{path.RouteKey}:{path.SourceId}:{string.Join('>', path.Nodes)}";

    [Fact]
    public void BranchesThatRejoinTheSameOutputRemainSeparatePathsAndSourcesAreSharedAcrossRoutes()
    {
        var diamond = new RouteTopology("diamond", true, 1, [Input(), Map("a"), Map("b"), Out()],
            [new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out")]);
        var result = Compile(Straight("other"), diamond);
        Assert.Equal(3, result.Paths.Length);
        Assert.Equal(3, result.SourcePathCounts[1]);
        Assert.Equal(["diamond:1:in>a>out", "diamond:1:in>b>out", "other:1:in>out"], result.Paths.Select(Path));
    }

    [Fact]
    public void MultiSourceInputPreservesEveryIdentityAndIgnoresFieldOrderAndUnitDifferences()
    {
        var catalog = Sources(2);
        catalog[1] = catalog[1] with { Fields = [new("value", FieldType.Number, "V"), new("ready", FieldType.Boolean, null)] };
        catalog[2] = catalog[2] with { Fields = [new("ready", FieldType.Boolean, null), new("value", FieldType.Number, "mV")] };
        var route = Straight("route", 2, 1);
        var result = RouteTopologyCompiler.Compile([route], catalog, Token);
        Assert.Equal(["route:1:in>out", "route:2:in>out"], result.Paths.Select(Path));
        Assert.All(result.SourcePathCounts.Values, count => Assert.Equal(1, count));
        Assert.Equal(result.Paths.Select(Path), RouteTopologyCompiler.Compile(
            [route with { Nodes = route.Nodes.Reverse().ToImmutableArray(), Edges = route.Edges.Reverse().ToImmutableArray() }], catalog, Token).Paths.Select(Path));
    }

    [Fact]
    public void SchemaMismatchReportsTheExactSourceNodeAndField()
    {
        var catalog = Sources(2);
        catalog[2] = catalog[2] with { Fields = [new("value", FieldType.Integer, null)] };
        var error = Assert.Throws<RouteTopologyException>(() => RouteTopologyCompiler.Compile([Straight("route", 1, 2)], catalog, Token));
        Assert.Equal(new RouteTopologyIssue("source.schemaMismatch", "route", "in", 2, FieldKey: "value"), error.Issue);
    }

    [Fact]
    public void DisabledMalformedRoutesAreNotCompiledAndAllDisabledProducesEmptyPlanTopology()
    {
        var disabled = new RouteTopology("disabled", false, 99, default, default);
        Assert.Single(Compile(disabled, Straight()).Paths);
        Assert.Empty(Compile(disabled).Paths);
    }

    [Theory]
    [InlineData("version", "route.version")]
    [InlineData("nodeVersion", "node.version")]
    [InlineData("kind", "node.kind")]
    [InlineData("duplicateNode", "node.duplicate")]
    [InlineData("duplicateSource", "source.duplicate")]
    [InlineData("noSources", "sources.nodeLimit")]
    [InlineData("unexpectedSource", "source.unexpected")]
    [InlineData("missingEdge", "edge.missing")]
    [InlineData("duplicateEdge", "edge.duplicate")]
    [InlineData("direction", "edge.direction")]
    [InlineData("dangling", "node.dangling")]
    [InlineData("cycle", "graph.cycle")]
    public void MalformedGraphsFailWithStructuredLocations(string scenario, string code)
    {
        var route = Straight();
        route = scenario switch
        {
            "version" => route with { SchemaVersion = 2 },
            "nodeVersion" => route with { Nodes = [Input() with { Version = 2 }, Out()] },
            "kind" => route with { Nodes = [Input() with { Kind = "input.script" }, Out()] },
            "duplicateNode" => route with { Nodes = [Input(), Input(), Out()] },
            "duplicateSource" => Straight("route", 1, 1),
            "noSources" => route with { Nodes = [Input() with { SourceIds = [] }, Out()] },
            "unexpectedSource" => route with { Nodes = [Input(), Out() with { SourceIds = [1] }] },
            "missingEdge" => route with { Edges = [new("in", "missing")] },
            "duplicateEdge" => route with { Edges = [new("in", "out"), new("in", "out")] },
            "direction" => route with { Edges = [new("out", "in")] },
            "dangling" => route with { Edges = [] },
            _ => route with { Nodes = [Input(), Map("a"), Map("b"), Out()], Edges = [new("in", "a"), new("a", "b"), new("b", "a"), new("b", "out")] }
        };
        var error = Assert.Throws<RouteTopologyException>(() => Compile(route));
        Assert.Equal(code, error.Issue.Code);
        Assert.Equal("route", error.Issue.RouteKey);
        if (code.StartsWith("edge.", StringComparison.Ordinal))
        { Assert.NotNull(error.Issue.EdgeIndex); }
    }

    [Theory]
    [InlineData("missing", "source.missing")]
    [InlineData("disabled", "source.disabled")]
    [InlineData("kind", "source.kind")]
    [InlineData("fields", "source.fields")]
    [InlineData("identity", "source.identityDuplicate")]
    public void InvalidReferencedSourcesCannotEnterTheCompiledTopology(string scenario, string expected)
    {
        var catalog = Sources(2);
        catalog[2] = scenario switch
        {
            "disabled" => catalog[2] with { Enabled = false },
            "kind" => catalog[2] with { UnitId = RuntimeUnitId.Create("input.http", "device2/values") },
            "fields" => catalog[2] with { Fields = [] },
            "identity" => catalog[2] with { UnitId = catalog[1].UnitId },
            _ => catalog[2]
        };
        if (scenario == "missing")
        { catalog.Remove(2); }
        var error = Assert.Throws<RouteTopologyException>(() => RouteTopologyCompiler.Compile([Straight("route", 1, 2)], catalog, Token));
        Assert.Equal(expected, error.Issue.Code);
        Assert.Equal(2, error.Issue.SourceId);
    }

    [Fact]
    public void RepeatedSourceReferencesAcrossNodesAndRoutesCountTowardTheSameLimit()
    {
        var routes = Enumerable.Range(0, 32).Select(index => Straight($"r{index:D2}")).ToArray();
        Assert.Equal(32, Compile(routes).SourcePathCounts[1]);
        var error = Assert.Throws<RouteTopologyException>(() => Compile([.. routes, Straight("r32")]));
        Assert.Equal("paths.sourceLimit", error.Issue.Code);
        Assert.Equal("r32", error.Issue.RouteKey);
        Assert.Equal(1, error.Issue.SourceId);
        var twoInputs = new RouteTopology("two", true, 1, [Input("a"), Input("b"), Out()], [new("a", "out"), new("b", "out")]);
        Assert.Equal(2, Compile(twoInputs).SourcePathCounts[1]);
    }

    [Fact]
    public void FiftyNodeDiamondWithSixteenMillionPotentialPathsIsRejectedBySaturatedCounting()
    {
        var nodes = new List<RouteTopologyNode> { Input() };
        var edges = new List<RouteTopologyEdge>();
        string[] previous = ["in"];
        for (var level = 0; level < 24; level++)
        {
            string[] next = [$"a{level}", $"b{level}"];
            nodes.AddRange(next.Select(Map));
            foreach (var from in previous)
            { foreach (var to in next) { edges.Add(new(from, to)); } }
            previous = next;
        }
        nodes.Add(Out());
        edges.AddRange(previous.Select(id => new RouteTopologyEdge(id, "out")));
        var route = new RouteTopology("exponential", true, 1, nodes.ToImmutableArray(), edges.ToImmutableArray());
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<RouteTopologyException>(() => Compile(route));
        output.WriteLine($"50 nodes / {edges.Count} edges / 2^24 potential paths: {watch.Elapsed.TotalMilliseconds:F3} ms");
        Assert.Equal("paths.sourceLimit", error.Issue.Code);
        Assert.Equal("in", error.Issue.NodeId);
    }

    [Fact]
    public void GlobalTwentyThousandPathBoundaryIsCheckedBeforeExpansion()
    {
        static RouteTopology Fan(string key, IEnumerable<long> ids) => new(key, true, 1,
            [Input("in", ids.ToArray()), .. Enumerable.Range(0, 32).Select(index => Out($"o{index:D2}"))],
            Enumerable.Range(0, 32).Select(index => new RouteTopologyEdge("in", $"o{index:D2}")).ToImmutableArray());
        var first = Fan("first", Enumerable.Range(1, 500).Select(value => (long)value));
        var second = Fan("second", Enumerable.Range(501, 125).Select(value => (long)value));
        var result = RouteTopologyCompiler.Compile([first, second], Sources(626), Token);
        Assert.Equal(20000, result.Paths.Length);
        Assert.All(result.SourcePathCounts.Values, count => Assert.Equal(32, count));
        var overflow = Fan("second", Enumerable.Range(501, 126).Select(value => (long)value));
        Assert.Equal("paths.totalLimit", Assert.Throws<RouteTopologyException>(() => RouteTopologyCompiler.Compile([first, overflow], Sources(626), Token)).Issue.Code);
    }

    [Fact]
    public void InputNodeAndGlobalSourceLimitsAreIndependentOfUnusedCatalogEntries()
    {
        var catalog = Sources(2001);
        Assert.Equal("sources.nodeLimit", Assert.Throws<RouteTopologyException>(() => RouteTopologyCompiler.Compile(
            [Straight("too-many", Enumerable.Range(1, 501).Select(id => (long)id).ToArray())], catalog, Token)).Issue.Code);
        var routes = Enumerable.Range(0, 4).Select(index => Straight($"r{index}", Enumerable.Range(index * 500 + 1, 500).Select(id => (long)id).ToArray())).ToArray();
        Assert.Equal(2000, RouteTopologyCompiler.Compile(routes, catalog, Token).Paths.Length);
        Assert.Equal("sources.totalLimit", Assert.Throws<RouteTopologyException>(() => RouteTopologyCompiler.Compile([.. routes, Straight("r4", 2001)], catalog, Token)).Issue.Code);
    }

    [Fact]
    public void SmallRandomDagsMatchIndependentExhaustivePathOracle()
    {
        var random = new Random(9817);
        for (var trial = 0; trial < 100; trial++)
        {
            var nodes = new[] { Input(), Map("a"), Map("b"), Map("c"), Map("d"), Map("e"), Map("f"), Out() };
            var edges = new List<RouteTopologyEdge>();
            for (var from = 0; from < nodes.Length - 1; from++)
            {
                for (var to = from + 1; to < nodes.Length; to++)
                { if (to == from + 1 || random.Next(2) == 0) { edges.Add(new(nodes[from].Id, nodes[to].Id)); } }
            }
            var expected = new List<string>();
            void Enumerate(string id, string chain)
            {
                var next = edges.Where(edge => edge.Source == id).ToArray();
                if (next.Length == 0)
                { expected.Add(chain); }
                foreach (var edge in next)
                { Enumerate(edge.Target, chain + ">" + edge.Target); }
            }
            Enumerate("in", "in");
            var route = new RouteTopology("random", true, 1, nodes.ToImmutableArray(), edges.ToImmutableArray());
            if (expected.Count > 32)
            { Assert.Equal("paths.sourceLimit", Assert.Throws<RouteTopologyException>(() => Compile(route)).Issue.Code); }
            else
            { Assert.Equal(expected.Order(StringComparer.Ordinal), Compile(route).Paths.Select(path => string.Join('>', path.Nodes)).Order(StringComparer.Ordinal)); }
        }
    }

    [Fact]
    public void DifferentInputKindsCannotReuseTheSameDeviceAndSourceIdentity()
    {
        var catalog = Sources(2);
        catalog[2] = catalog[2] with { UnitId = RuntimeUnitId.Create("input.http", catalog[1].UnitId.Key) };
        var route = new RouteTopology("route", true, 1,
            [Input("a", 1), Input("b", 2) with { Kind = "input.http" }, Out()],
            [new("a", "out"), new("b", "out")]);
        var error = Assert.Throws<RouteTopologyException>(() => RouteTopologyCompiler.Compile([route], catalog, Token));
        Assert.Equal(new RouteTopologyIssue("source.identityDuplicate", "route", "b", 2), error.Issue);
    }

    [Fact]
    public void CycleLocationIsOnTheCycleRatherThanItsDownstreamOutput()
    {
        var route = new RouteTopology("route", true, 1, [Out("aaa"), Input(), Map("b"), Map("c")],
            [new("in", "b"), new("b", "c"), new("c", "b"), new("c", "aaa")]);
        var error = Assert.Throws<RouteTopologyException>(() => Compile(route));
        Assert.Equal("graph.cycle", error.Issue.Code);
        Assert.True(error.Issue.NodeId is "b" or "c");
    }

    [Fact]
    public void FiftyOneNodesAndDuplicateEnabledRouteKeysAreRejected()
    {
        var route = Straight() with { Nodes = [Input(), .. Enumerable.Range(0, 49).Select(index => Map($"m{index}")), Out()] };
        Assert.Equal("nodes.limit", Assert.Throws<RouteTopologyException>(() => Compile(route)).Issue.Code);
        Assert.Equal("route.key", Assert.Throws<RouteTopologyException>(() => Compile(Straight(), Straight())).Issue.Code);
    }

    [Fact]
    public void ReferencesAcrossMultipleNodesAndRoutesShareTheSamePathBudget()
    {
        var routes = Enumerable.Range(0, 31).Select(index => Straight($"r{index:D2}")).ToArray();
        var twoInputs = new RouteTopology("two", true, 1, [Input("a"), Input("b"), Out()], [new("a", "out"), new("b", "out")]);
        var error = Assert.Throws<RouteTopologyException>(() => Compile([.. routes, twoInputs]));
        Assert.Equal(new RouteTopologyIssue("paths.sourceLimit", "two", "b", 1), error.Issue);
    }

    [Fact]
    public void CancellationStopsCompilationBeforeReturningAnyPaths()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RouteTopologyCompiler.Compile([Straight()], Sources(), stop.Token));
    }
}

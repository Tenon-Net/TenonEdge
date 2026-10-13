using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.UnitTests.Routes;

public sealed class RoutePlanCompilerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static RouteGraphNode Node(string id, string type, string config) => new(id, type, 1, id, Json(config));
    private static RouteGraphNode Input(string id = "in", string sources = "[1]") => Node(id, "input.simulator", "{\"sourceIds\":" + sources + "}");
    private static RouteGraphNode Output(string id = "out") => Node(id, "output.http", "{\"connectionId\":10}");
    private static RouteGraphNode Map(string id, string from, string to) => Node(id, "transform.map", $$"""{"rules":[{"target":"{{to}}","type":"integer","sourceField":"{{from}}"}]}""");
    private static RouteDraft Route(RouteGraphNode[]? nodes = null, RouteGraphEdge[]? edges = null) => new(20, "route", "Route", null, true,
        new(1, nodes ?? [Input(), Output()], edges ?? [new("in", "out")], new([])), null, 0);
    private static RouteCompilationSnapshot Snapshot(RouteDraft? route = null, int sources = 1) => new("version", [route ?? Route()],
        Enumerable.Range(1, sources).ToDictionary(i => (long)i, i => new DeviceDraft(i, $"d{i}", "Device", DeviceKind.Simulator, null, null, null, ModbusAddressNotation.Offset, true)),
        Enumerable.Range(1, sources).ToDictionary(i => (long)i, i => new InputDraft(i, i, "values", "Values", DeviceKind.Simulator, true, null,
            Simulator: new(SampleJson: "{\"value\":42}"))),
        new Dictionary<long, ConnectionDraft> { [10] = new(10, "http", "HTTP", ConnectionKind.Http, true, null, null, new("http://localhost/")) }, new HashSet<long>());
    private static RoutePlanCompilation Compile(RouteCompilationSnapshot snapshot) => RoutePlanCompiler.Compile(snapshot, Token);

    [Fact]
    public void RejoiningBranchesProduceDistinctTargetsButSourcesAreCompiledOnlyOnceAndHashesIgnoreLayout()
    {
        var route = Route([Input(sources: "[2,1]"), Map("a", "value", "a"), Map("b", "value", "b"), Output()],
            [new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out")]);
        var snapshot = Snapshot(route, 2);
        var result = Compile(snapshot);
        Assert.Equal(4, result.Topology.Paths.Length);
        Assert.Equal(2, result.Document.Units.Count(unit => unit.Id.Kind == "input.simulator"));
        var outputs = result.Document.Units.Where(unit => unit.Id.Kind == "output.http").ToArray();
        Assert.Equal(2, outputs.Length);
        Assert.All(outputs, unit => Assert.Equal(2, unit.Configuration.GetProperty("inputs").GetArrayLength()));
        Assert.Equal(2, result.Paths.Select(path => path.OutputId).Distinct().Count());
        var changed = route with { Name = "Changed", Graph = route.Graph with { Nodes = route.Graph.Nodes.Reverse().ToArray(), Edges = route.Graph.Edges.Reverse().ToArray(), Layout = new(new() { ["in"] = new(999, 999) }) } };
        Assert.Equal(result.Document.ContentHash, Compile(snapshot with { Routes = [changed] }).Document.ContentHash);
    }

    [Fact]
    public void OneSourceReferencedByDifferentRoutesStillGeneratesOnlyOneInputUnit()
    {
        var snapshot = Snapshot();
        var result = Compile(snapshot with { Routes = [snapshot.Routes[0], snapshot.Routes[0] with { Id = 21, Key = "other" }] });
        Assert.Equal(2, result.Topology.SourcePathCounts[1]);
        Assert.Single(result.Document.Units, unit => unit.Id.Kind == "input.simulator");
        Assert.Equal(2, result.Document.Units.Count(unit => unit.Id.Kind == "output.http"));
        Assert.Equal(2, result.Paths.Select(path => path.OutputId).Distinct().Count());
    }

    [Fact]
    public void SequentialFieldPropagationRejectsReferencesToFieldsRemovedByEarlierStages()
    {
        var route = Route([Input(), Map("first", "value", "renamed"), Map("second", "renamed", "final"), Output()],
            [new("in", "first"), new("first", "second"), new("second", "out")]);
        var result = Compile(Snapshot(route));
        Assert.Equal("final", Assert.Single(Assert.Single(result.Paths).Fields).Key);
        Assert.Equal(2, result.Document.Units.Single(unit => unit.Id.Kind == "output.http").Configuration.GetProperty("mappingStages").GetArrayLength());
        route.Graph.Nodes[2] = Map("second", "value", "wrong");
        var issue = Assert.Throws<RouteTopologyException>(() => Compile(Snapshot(route))).Issue;
        Assert.Equal(new RouteTopologyIssue("field.missing", "route", "second", 1, FieldKey: "value"), issue);
    }

    [Fact]
    public void FanInMustSatisfyEveryUpstreamSchemaAndCustomOutputFieldsAreCheckedCaseInsensitively()
    {
        var route = Route([Input("a"), Input("b", "[2]"), Map("map", "value", "result"), Output()],
            [new("a", "map"), new("b", "map"), new("map", "out")]);
        var snapshot = Snapshot(route, 2);
        var inputs = snapshot.Inputs.ToDictionary();
        inputs[2] = inputs[2] with { Simulator = new(SampleJson: "{\"other\":3}") };
        var issue = Assert.Throws<RouteTopologyException>(() => Compile(snapshot with { Inputs = inputs })).Issue;
        Assert.Equal(new RouteTopologyIssue("field.missing", "route", "map", 2, FieldKey: "value"), issue);
        var output = Node("out", "output.http", """{"connectionId":10,"payload":{"Mode":"custom","Fields":[{"Target":"x","SourceField":"absent"}]}}""");
        issue = Assert.Throws<RouteTopologyException>(() => Compile(Snapshot(Route([Input(), output])))).Issue;
        Assert.Equal(new RouteTopologyIssue("field.missing", "route", "out", 1, FieldKey: "absent"), issue);
    }

    [Theory]
    [InlineData("missing", "output.connectionMissing")]
    [InlineData("disabled", "output.connectionDisabled")]
    [InlineData("kind", "output.connectionKind")]
    [InlineData("tenon", "output.unavailable")]
    public void OutputReferencesCannotSilentlyFallBackToAnotherProtocol(string scenario, string expected)
    {
        var snapshot = Snapshot();
        var connections = snapshot.Connections.ToDictionary();
        if (scenario == "missing")
        { connections.Clear(); }
        if (scenario == "disabled")
        { connections[10] = connections[10] with { Enabled = false }; }
        if (scenario == "kind")
        { connections[10] = new(10, "mqtt", "MQTT", ConnectionKind.Mqtt, true, null, new("localhost"), null); }
        if (scenario == "tenon")
        { snapshot.Routes[0].Graph.Nodes[1] = Node("out", "output.tenon", "{\"connectionId\":10}"); }
        Assert.Equal(expected, Assert.Throws<RouteTopologyException>(() => Compile(snapshot with { Connections = connections })).Issue.Code);
    }

    [Fact]
    public void DisabledUnreferencedInputsDoNotPreventStopAllAndCancellationIsObserved()
    {
        var snapshot = Snapshot();
        var inputs = snapshot.Inputs.ToDictionary();
        inputs[1] = inputs[1] with { Enabled = false };
        Assert.Equal("source.disabled", Assert.Throws<RouteTopologyException>(() => Compile(snapshot with { Inputs = inputs })).Issue.Code);
        var stopped = Compile(snapshot with { Routes = [snapshot.Routes[0] with { Enabled = false }], Inputs = inputs });
        Assert.Empty(stopped.Topology.Paths);
        var queue = Assert.Single(stopped.Document.Units);
        Assert.True(queue.Configuration.GetProperty("queueOnly").GetBoolean());
        Assert.Empty(queue.Configuration.GetProperty("inputs").EnumerateArray());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => RoutePlanCompiler.Compile(snapshot, stop.Token));
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("http")]
    [InlineData("modbus")]
    public void SavedProtocolSourcesLowerToStrictlyParsableUnitsWithTheirDeclaredFields(string protocol)
    {
        var snapshot = Snapshot();
        var kind = Enum.Parse<DeviceKind>(protocol, true);
        var device = snapshot.Devices[1] with { Kind = kind, ConnectionId = 11, UnitId = kind == DeviceKind.Modbus ? 0 : null };
        var input = snapshot.Inputs[1] with { Kind = kind, Simulator = null };
        var fields = new[] { new JsonSourceField("value", "/n", FieldType.Integer, Unit: "V") };
        ConnectionDraft connection;
        if (kind == DeviceKind.Mqtt)
        {
            input = input with { Mqtt = new("meters/+/data", fields) };
            connection = new(11, "mqtt", "MQTT", ConnectionKind.Mqtt, true, null, new("localhost"), null);
        }
        else if (kind == DeviceKind.Http)
        {
            input = input with { Http = new("read", fields, Headers: new() { ["X-Site"] = "source" }) };
            connection = new(11, "http-source", "HTTP", ConnectionKind.Http, true, null, null, new("http://localhost/", Headers: new() { ["X-Site"] = "connection" }));
        }
        else
        {
            input = input with { Modbus = new(1000, Enumerable.Range(0, 1001).Select(i => new ModbusPointSettings { Key = $"v{i}", FunctionCode = 3, Address = i, Type = "uint16", Unit = "V" }).ToArray()) };
            connection = new(11, "modbus", "Modbus", ConnectionKind.Modbus, true, new("localhost"), null, null);
        }
        var connections = snapshot.Connections.ToDictionary();
        connections.Add(11, connection);
        snapshot.Routes[0].Graph.Nodes[0] = Node("in", "input." + protocol, "{\"sourceIds\":[1]}");
        var result = Compile(snapshot with { Devices = new Dictionary<long, DeviceDraft> { [1] = device }, Inputs = new Dictionary<long, InputDraft> { [1] = input }, Connections = connections, MqttCredentials = new HashSet<long> { 11 } });
        var unit = result.Document.Units.Single(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal));
        Assert.Equal("d1/values", unit.Id.Key);
        Assert.False(unit.Configuration.TryGetProperty("recipient", out _));
        Assert.False(unit.Configuration.TryGetProperty("baseUri", out _));
        if (kind == DeviceKind.Mqtt)
        { Assert.True(unit.Configuration.GetProperty("usePassword").GetBoolean()); }
        if (kind == DeviceKind.Http)
        { Assert.Equal("source", unit.Configuration.GetProperty("headers").GetProperty("X-Site").GetString()); }
        Assert.Equal(kind == DeviceKind.Modbus ? 1001 : 1, Assert.Single(result.Paths).Fields.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedMqttInputOutputKeepCredentialRequirementAndRejectFeedback(bool feedback)
    {
        var snapshot = Snapshot();
        var device = snapshot.Devices[1] with { Kind = DeviceKind.Mqtt, ConnectionId = 11 };
        var input = snapshot.Inputs[1] with { Kind = DeviceKind.Mqtt, Simulator = null, Mqtt = new("meters/+/data", [new("value", "/n", FieldType.Integer)]) };
        var topic = feedback ? "meters/a/data" : "forwarded/{deviceKey}";
        var route = Route([Node("in", "input.mqtt", "{\"sourceIds\":[1]}"),
            Node("out", "output.mqtt", JsonSerializer.Serialize(new { connectionId = 11, topic, qos = 0 }))]);
        snapshot = snapshot with
        {
            Routes = [route],
            Devices = new Dictionary<long, DeviceDraft> { [1] = device },
            Inputs = new Dictionary<long, InputDraft> { [1] = input },
            Connections = new Dictionary<long, ConnectionDraft> { [11] = new(11, "mqtt", "MQTT", ConnectionKind.Mqtt, true, null, new("localhost"), null) },
            MqttCredentials = new HashSet<long> { 11 },
        };
        if (feedback)
        { Assert.Equal("plan.configuration", Assert.Throws<RouteTopologyException>(() => Compile(snapshot)).Issue.Code); }
        else
        {
            var result = Compile(snapshot);
            Assert.All(result.Document.Units, unit => Assert.True(unit.Configuration.GetProperty("usePassword").GetBoolean()));
            Assert.Equal(0, result.Document.Units.Single(unit => unit.Id.Kind == "output.mqtt").Configuration.GetProperty("qos").GetInt32());
        }
    }

    [Fact]
    public void LoweringPreservesExactSourceSamplesAndNormalizesConfigurationPropertyNames()
    {
        var snapshot = Snapshot();
        var inputs = snapshot.Inputs.ToDictionary();
        inputs[1] = inputs[1] with { Simulator = new(SampleJson: "{\"value\":9223372036854775807}") };
        var route = Route([Input(), Node("map", "transform.map", """{"Rules":[{"Target":"n","Type":"integer","SourceField":"value"}]}"""), Output()],
            [new("in", "map"), new("map", "out")]);
        var result = Compile(snapshot with { Routes = [route], Inputs = inputs });
        Assert.Equal("9223372036854775807", result.Document.Units.Single(unit => unit.Id.Kind == "input.simulator").Configuration.GetProperty("sample").GetProperty("value").GetRawText());
        Assert.Equal("n", Assert.Single(Assert.Single(result.Paths).Fields).Key);
    }

    [Fact]
    public void ExpansionStopsAtTheDocumentBudgetInsteadOfDuplicatingUnlimitedConfigurations()
    {
        var map = new RouteGraphNode("map", "transform.map", 1, "large", JsonSerializer.SerializeToElement(new
        { rules = Enumerable.Range(0, 16).Select(i => new { target = $"v{i}", type = "string", constant = new string('x', 32768) }).ToArray() }));
        var outputs = Enumerable.Range(0, 32).Select(i => Output($"out{i}")).ToArray();
        var route = Route([Input(), map, .. outputs], [new("in", "map"), .. outputs.Select(node => new RouteGraphEdge("map", node.Id))]);
        Assert.Equal("plan.size", Assert.Throws<RouteTopologyException>(() => Compile(Snapshot(route))).Issue.Code);
    }
}

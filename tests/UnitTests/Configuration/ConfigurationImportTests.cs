using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.UnitTests.Configuration;

public sealed class ConfigurationImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static readonly ConfigurationCheckpoint Empty = new("local-version", [], [], [], []);
    private static PortableConfiguration File() => new(1,
        [new("receiver", "Receiver", ConnectionKind.Http, true, null, null, new("https://example.com/base/"), new("requiresInput", "sha256:" + new string('a', 64)))],
        [new("meter", "Meter", DeviceKind.Simulator, "floor", null, null, ModbusAddressNotation.Offset, true)],
        [new("meter", "values", "Values", DeviceKind.Simulator, true, null, null, null, new(SampleJson: "{\"value\":9223372036854775807}"))],
        [new("route", "Route", "Portable", false, new(1,
            [new("source", "input.simulator", 1, "Source", Json("""{"sourceKeys":["meter/values"]}""")),
             new("target", "output.http", 1, "Target", Json("""{"connectionKey":"receiver","path":"data","payload":{"mode":"custom","fields":[{"target":"id","constant":9223372036854775807}]}}"""))],
            [new("source", "target")], new(new() { ["source"] = new(12, 34) })))]);

    private static ConfigurationImportPlan Prepare(PortableConfiguration file, ConfigurationCheckpoint? before = null,
        ConfigurationImportMode mode = ConfigurationImportMode.Merge)
    {
        long id = 1000;
        return ConfigurationImportPlanner.Prepare(file, before ?? Empty, mode, () => ++id, Token);
    }

    [Fact]
    public void ExportedFileRoundTripsThroughStrictReaderAndAllocatesOnlyLocalIdsWithExactNumbers()
    {
        var file = File();
        var parsed = ConfigurationImportDocument.Read(ConfigurationExport.Serialize(file, Token), Token);
        var result = Prepare(parsed);
        Assert.Equal([1001L, 1002, 1003, 1004], new[] { result.Draft.Connections[0].Id, result.Draft.Devices[0].Id, result.Draft.Inputs[0].Id, result.Draft.Routes[0].Id });
        Assert.Equal(1002, result.Draft.Inputs[0].DeviceId);
        Assert.Equal(1003, result.Draft.Routes[0].Graph.Nodes[0].Config.GetProperty("sourceIds")[0].GetInt64());
        Assert.Equal(1001, result.Draft.Routes[0].Graph.Nodes[1].Config.GetProperty("connectionId").GetInt64());
        Assert.Equal("9223372036854775807", result.Draft.Routes[0].Graph.Nodes[1].Config.GetProperty("payload").GetProperty("fields")[0].GetProperty("constant").GetRawText());
        Assert.Equal(file.Inputs[0].Simulator!.SampleJson, result.Draft.Inputs[0].Simulator!.SampleJson);
        Assert.Equal(new RouteNodePosition(12, 34), result.Draft.Routes[0].Graph.Layout.Nodes["source"]);
        Assert.All(result.Changes, change => Assert.Equal("added", change.Change));
        Assert.Equal(4, result.Changes.Length);
        Assert.Equal("local-version", result.Draft.Version);
        Assert.Empty(Empty.Connections);
    }

    [Theory]
    [InlineData(ConfigurationImportMode.Merge)]
    [InlineData(ConfigurationImportMode.Replace)]
    internal void MatchingBusinessKeysPreserveLocalIdsAndRouteReplacementIsWhole(ConfigurationImportMode mode)
    {
        var original = Prepare(File()).Draft;
        original = original with
        {
            Connections = [.. original.Connections, new(501, "other", "Other", ConnectionKind.Mqtt, false, null, new("broker.example.com"), null)],
            Routes = [original.Routes[0] with { Graph = new(1, [], [], new([])) }],
            Edits = [new("connection", 501, "original-stamp", "original-editor", 123, false)],
        };
        var originalJson = JsonSerializer.Serialize(original);
        var called = false;
        var result = ConfigurationImportPlanner.Prepare(File(), original, mode, () => { called = true; return 777; }, Token);
        Assert.False(called);
        Assert.Equal(1001, result.Draft.Connections.Single(value => value.Key == "receiver").Id);
        Assert.Equal(1002, result.Draft.Devices[0].Id);
        Assert.Equal(1003, result.Draft.Inputs[0].Id);
        Assert.Equal(1004, result.Draft.Routes[0].Id);
        Assert.Equal(2, result.Draft.Routes[0].Graph.Nodes.Length);
        Assert.Equal(mode == ConfigurationImportMode.Merge ? 2 : 1, result.Draft.Connections.Length);
        Assert.Contains(result.Changes, change => change.Kind == "route" && change.Change == "modified");
        Assert.Equal(mode == ConfigurationImportMode.Replace, result.Changes.Any(change => change.Id == 501 && change.Change == "removed"));
        Assert.Equal(originalJson, JsonSerializer.Serialize(original));
    }

    [Fact]
    public void MergeCanResolveObjectsAbsentFromFileButReplaceCannotBorrowThemFromLocalDraft()
    {
        var before = Prepare(File()).Draft;
        var file = File() with { Connections = [], Devices = [], Inputs = [] };
        Assert.Single(Prepare(file, before).Draft.Routes);
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(file, before, ConfigurationImportMode.Replace));
        Assert.Equal("import.referenceMissing", error.Issues[0].Code);
        Assert.Equal("$.routes[0].graph.nodes[0].config.sourceKeys[0]", error.Issues[0].Path);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("device")]
    [InlineData("input")]
    [InlineData("route")]
    public void DuplicateBusinessKeysReportBothPositions(string kind)
    {
        var file = File();
        file = kind switch
        {
            "connection" => file with { Connections = [file.Connections[0], file.Connections[0]] },
            "device" => file with { Devices = [file.Devices[0], file.Devices[0]] },
            "input" => file with { Inputs = [file.Inputs[0], file.Inputs[0]] },
            _ => file with { Routes = [file.Routes[0], file.Routes[0]] },
        };
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(file));
        Assert.Equal(2, error.Issues.Length);
        Assert.All(error.Issues, issue => Assert.Equal("import.duplicateKey", issue.Code));
        Assert.Contains("[0]", error.Issues[0].Path, StringComparison.Ordinal);
        Assert.Contains("[1]", error.Issues[1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceKeysAreDeviceScopedButFieldKeysAreUniqueAcrossAllSourcesOnTheDevice()
    {
        var file = File() with
        {
            Devices = [new("meter", "Meter", DeviceKind.Http, null, "receiver", null, ModbusAddressNotation.Offset, true)],
            Inputs = [new("meter", "fast", "Fast", DeviceKind.Http, true, null, null,
                new("status", [new("value", "/value", FieldType.Number)]), null),
                new("meter", "slow", "Slow", DeviceKind.Http, false, null, null,
                    new("status", [new("value", "/value", FieldType.Number)]), null)],
            Routes = [],
        };
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(file));
        Assert.Equal(["$.inputs[0].http.fields[0].key", "$.inputs[1].http.fields[0].key"], error.Issues.Select(issue => issue.Path));
        Assert.All(error.Issues, issue => Assert.Equal("import.duplicateFieldKey", issue.Code));
        file = file with
        {
            Devices = [.. file.Devices, file.Devices[0] with { DeviceKey = "second" }],
            Inputs = [file.Inputs[0], file.Inputs[0] with { DeviceKey = "second" }],
        };
        Assert.Equal(2, Prepare(file).Draft.Inputs.Length);
    }

    [Theory]
    [InlineData("{\"sourceIds\":[1003]}")]
    [InlineData("{\"sourceKeys\":[\"meter/values\"],\"sourceIds\":[1003]}")]
    public void FileCannotSmuggleLocalResourceIdsIntoGraph(string config)
    {
        var file = File();
        file.Routes[0].Graph.Nodes[0] = file.Routes[0].Graph.Nodes[0] with { Config = Json(config) };
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(file));
        Assert.Equal("import.internalReference", error.Issues[0].Code);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("query")]
    [InlineData("userinfo")]
    [InlineData("module")]
    [InlineData("hint")]
    public void SecretBypassesUnknownModulesAndMalformedCredentialHintsAreRejected(string kind)
    {
        var file = File();
        if (kind == "header")
        { file.Connections[0] = file.Connections[0] with { Http = new("https://example.com/", Headers: new() { ["Authorization"] = "Bearer fixture" }) }; }
        if (kind == "query")
        { file.Routes[0].Graph.Nodes[1] = file.Routes[0].Graph.Nodes[1] with { Config = Json("""{"connectionKey":"receiver","path":"data?api_key=fixture"}""") }; }
        if (kind == "userinfo")
        { file.Connections[0] = file.Connections[0] with { Http = new("https://user:fixture@example.com/") }; }
        if (kind == "module")
        { file.Routes[0].Graph.Nodes[1] = file.Routes[0].Graph.Nodes[1] with { Type = "output.missing" }; }
        if (kind == "hint")
        { file.Connections[0] = file.Connections[0] with { Credential = new("keepExisting", "not-a-digest") }; }
        var before = Prepare(File()).Draft;
        var snapshot = JsonSerializer.Serialize(before);
        Assert.Throws<ConfigurationImportException>(() => Prepare(file, before));
        Assert.Equal(snapshot, JsonSerializer.Serialize(before));
    }

    [Fact]
    public void CombinedMergeValidationRejectsADeviceProtocolChangeThatWouldInvalidateAnExistingSource()
    {
        var before = Prepare(File()).Draft;
        var file = File() with
        {
            Devices = [new("meter", "Meter", DeviceKind.Http, null, "receiver", null, ModbusAddressNotation.Offset, true)],
            Inputs = [],
            Routes = [],
        };
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(file, before));
        Assert.Equal("import.protocolMismatch", error.Issues[0].Code);
    }

    [Fact]
    public void EmptyImportMergesWithoutChangesOrReplacesWithExplicitDeletions()
    {
        var before = Prepare(File()).Draft;
        var empty = new PortableConfiguration(1, [], [], [], []);
        Assert.Empty(Prepare(empty, before).Changes);
        var replaced = Prepare(empty, before, ConfigurationImportMode.Replace);
        Assert.Equal(4, replaced.Changes.Length);
        Assert.All(replaced.Changes, change => Assert.Equal("removed", change.Change));
        Assert.Empty(replaced.Draft.Connections);
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"connections\":[],\"devices\":[],\"inputs\":[],\"routes\":[]}", "import.formatVersion")]
    [InlineData("{\"formatVersion\":\"1\"}", "import.formatVersion")]
    [InlineData("{\"formatVersion\":1,\"formatVersion\":1}", "import.duplicateProperty")]
    [InlineData("{\"formatVersion\":1,\"connections\":[],\"devices\":[],\"inputs\":[],\"routes\":[],\"secret\":\"fixture\"}", "import.schema")]
    [InlineData("{\"formatVersion\":1,\"connections\":null,\"devices\":[],\"inputs\":[],\"routes\":[]}", "import.schema")]
    [InlineData("{\"formatVersion\":1,\"Connections\":[],\"devices\":[],\"inputs\":[],\"routes\":[]}", "import.schema")]
    [InlineData("{\"formatVersion\":1,\"connections\":[],\"devices\":[],\"inputs\":[]}", "import.schema")]
    [InlineData("[]", "import.schema")]
    [InlineData("", "import.schema")]
    public void StrictJsonRejectsVersionSchemaAliasesMissingAndDuplicateProperties(string raw, string code)
    {
        var error = Assert.Throws<ConfigurationImportException>(() => ConfigurationImportDocument.Read(Encoding.UTF8.GetBytes(raw), Token));
        Assert.Equal(code, error.Issues[0].Code);
        Assert.DoesNotContain("fixture", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedDuplicateJsonPropertyIsNotSilentlyOverwritten()
    {
        var raw = Encoding.UTF8.GetString(ConfigurationExport.Serialize(File(), Token)).Replace("\"x\":12", "\"x\":12,\"x\":34", StringComparison.Ordinal);
        var error = Assert.Throws<ConfigurationImportException>(() => ConfigurationImportDocument.Read(Encoding.UTF8.GetBytes(raw), Token));
        Assert.Equal("import.duplicateProperty", error.Issues[0].Code);
        Assert.Contains("[\"layout\"][\"nodes\"][\"source\"][\"x\"]", error.Issues[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void FileByteLimitIsExactAndErrorListsAreBoundedAndExplicitlyTruncated()
    {
        var raw = ConfigurationExport.Serialize(new(1, [], [], [], []), Token);
        var padded = new byte[ConfigurationExport.MaximumBytes];
        Array.Fill(padded, (byte)' ');
        raw.CopyTo(padded, 0);
        Assert.Empty(ConfigurationImportDocument.Read(padded, Token).Connections);
        Assert.Equal("import.fileTooLarge", Assert.Throws<ConfigurationImportException>(() => ConfigurationImportDocument.Read(new byte[padded.Length + 1], Token)).Issues[0].Code);
        var repeated = File() with { Connections = Enumerable.Repeat(File().Connections[0], 200).ToArray() };
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(repeated));
        Assert.Equal(128, error.Issues.Length);
        Assert.True(error.Truncated);
    }

    [Fact]
    public void IncompleteGraphsKeepExplicitEmptySourcesAndNullOutputInsteadOfInventingReferences()
    {
        var file = File();
        file.Routes[0].Graph.Nodes[0] = file.Routes[0].Graph.Nodes[0] with { Config = Json("""{"sourceKeys":[]}""") };
        file.Routes[0].Graph.Nodes[1] = file.Routes[0].Graph.Nodes[1] with { Config = Json("""{"connectionKey":null}""") };
        var result = Prepare(file);
        Assert.Empty(result.Draft.Routes[0].Graph.Nodes[0].Config.GetProperty("sourceIds").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.Draft.Routes[0].Graph.Nodes[1].Config.GetProperty("connectionId").ValueKind);
    }

    [Theory]
    [InlineData("input", "import.protocolMismatch")]
    [InlineData("output", "import.protocolMismatch")]
    [InlineData("edge", "import.referenceMissing")]
    [InlineData("tenon", "import.moduleUnavailable")]
    public void GraphReferencesMustExistAndMatchInstalledProtocolModules(string target, string code)
    {
        var file = File();
        if (target == "input")
        { file.Routes[0].Graph.Nodes[0] = file.Routes[0].Graph.Nodes[0] with { Type = "input.http" }; }
        if (target == "output")
        {
            file.Routes[0].Graph.Nodes[1] = file.Routes[0].Graph.Nodes[1] with
            { Type = "output.mqtt", Config = Json("""{"connectionKey":"receiver","topic":"meter/data"}""") };
        }
        if (target == "edge")
        { file.Routes[0].Graph.Edges[0] = new("source", "missing"); }
        if (target == "tenon")
        {
            file.Routes[0].Graph.Nodes[1] = file.Routes[0].Graph.Nodes[1] with
            { Type = "output.tenon", Config = Json("""{"connectionKey":null}""") };
        }
        Assert.Equal(code, Assert.Throws<ConfigurationImportException>(() => Prepare(file)).Issues[0].Code);
    }

    [Fact]
    public void MissingOutputReferenceIdentifiesTheImportedJsonLocation()
    {
        var file = File() with { Connections = [] };
        var error = Assert.Throws<ConfigurationImportException>(() => Prepare(file));
        Assert.Equal("import.referenceMissing", error.Issues[0].Code);
        Assert.Equal("$.routes[0].graph.nodes[1].config.connectionKey", error.Issues[0].Path);
    }

    [Fact]
    public void CancelledPlanningAndInvalidLocalIdAllocationCannotYieldACandidate()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConfigurationImportDocument.Read(ReadOnlyMemory<byte>.Empty, stop.Token));
        Assert.Throws<OperationCanceledException>(() => ConfigurationImportPlanner.Prepare(File(), Empty, ConfigurationImportMode.Merge, () => 1, stop.Token));
        Assert.Throws<InvalidOperationException>(() => ConfigurationImportPlanner.Prepare(File(), Empty, ConfigurationImportMode.Merge, () => 0, Token));
        Assert.Throws<InvalidOperationException>(() => ConfigurationImportPlanner.Prepare(File(), Empty, ConfigurationImportMode.Merge, () => 1, Token));
    }
}

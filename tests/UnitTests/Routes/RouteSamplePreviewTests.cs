using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Routes;

public sealed class RouteSamplePreviewTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Time = new(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static RouteGraphNode Node(string id, string kind, string config) => new(id, kind, 1, id, Json(config));
    private static RouteGraphNode Input(string sources = "[1]") => Node("in", "input.simulator", "{\"sourceIds\":" + sources + "}");
    private static RouteGraphNode Output(string id = "out", long connection = 10) => Node(id, "output.http", $$"""{"connectionId":{{connection}},"path":"telemetry"}""");
    private static RouteCompilationSnapshot Snapshot(RouteGraphNode[] nodes, RouteGraphEdge[] edges, string schema = "{\"value\":1}") =>
        new("version", [new(1, "route", "Route", null, false, new(1, nodes, edges, new([])), null, 0)],
            new Dictionary<long, DeviceDraft> { [1] = new(1, "d1", "Device", DeviceKind.Simulator, null, null, null, ModbusAddressNotation.Offset, true) },
            new Dictionary<long, InputDraft> { [1] = new(1, 1, "values", "Values", DeviceKind.Simulator, true, null, Simulator: new(SampleJson: schema)) },
            new Dictionary<long, ConnectionDraft>
            {
                [10] = new(10, "http", "HTTP", ConnectionKind.Http, true, null, null,
                new("http://localhost/", Headers: new Dictionary<string, string> { ["X-Static"] = "connection" }))
            }, new HashSet<long>());
    private static EdgeMessage Message(Dictionary<string, FieldValue>? values = null, Dictionary<string, QualityCode>? quality = null, RawJsonPayload? raw = null, string device = "d1") =>
        new(Guid.CreateVersion7(), "values", device, Time, TimeSource.Gateway, Time, values ?? new() { ["value"] = FieldValue.FromInt64(42) }, quality, raw);
    private static RouteSamplePreviewResult Run(RouteCompilationSnapshot snapshot, EdgeMessage sample) => RouteSamplePreview.Run(snapshot, 1, sample, Token);

    [Fact]
    public void SequentialMappingsKeepExactValuesAndUseTheFormalPayloadAndHttpIdempotencyHeader()
    {
        var first = Node("first", "transform.map", """{"rules":[{"target":"large","type":"integer","sourceField":"value"},{"target":"precise","type":"number","sourcePath":"/nested/decimal"}]}""");
        var second = Node("second", "transform.map", """{"rules":[{"target":"renamed","type":"integer","sourceField":"large"},{"target":"scaled","type":"number","sourceField":"precise","scale":1,"unit":"kW"}]}""");
        var output = Node("out", "output.http", """{"connectionId":10,"path":"telemetry","headers":{"X-Static":"node"}}""");
        var snapshot = Snapshot([Input(), first, second, output], [new("in", "first"), new("first", "second"), new("second", "out")]);
        var sample = Message(new() { ["value"] = FieldValue.FromInt64(long.MaxValue) }, raw: RawJsonPayload.CopyFrom(Json("""{"nested":{"decimal":0.1234567890123456789012345678}}""")));
        var result = Run(snapshot, sample);
        var path = Assert.Single(result.Paths);
        Assert.True(path.Succeeded);
        Assert.Equal(1, result.SucceededPaths);
        Assert.False(result.Truncated);
        Assert.Equal(["first", "second"], path.Stages.Select(stage => stage.NodeId));
        Assert.Equal("9223372036854775807", Assert.Single(path.Stages[0].Fields, field => field.Key == "large").Text);
        Assert.Equal("kW", Assert.Single(path.Stages[1].Fields, field => field.Key == "scaled").Unit);
        var payload = Assert.IsType<RoutePreviewOutput>(path.Output);
        Assert.Contains("0.1234567890123456789012345678", payload.Body, StringComparison.Ordinal);
        Assert.Equal("node", payload.Headers["X-Static"]);
        Assert.Equal("application/json", payload.Headers["Content-Type"]);
        Assert.Equal(payload.DeliveryId.ToString("D"), payload.Headers["Idempotency-Key"]);
        Assert.Equal("telemetry", payload.Address);
        Assert.NotEqual(sample.SourceMessageId, payload.DeliveryId);
        var mapped = FieldMappingConfiguration.Parse(first.Config.GetProperty("rules"))!.Apply(sample).Message!;
        mapped = FieldMappingConfiguration.Parse(second.Config.GetProperty("rules"))!.Apply(mapped).Message!;
        Assert.Equal(Encoding.UTF8.GetString(StandardEnvelope.Serialize(mapped, payload.DeliveryId)), payload.Body);
        Assert.Equal(sample.SourceMessageId, result.SourceMessageId);
        Assert.Equal(Time, result.CollectedAt);
        Assert.Single(sample.Values); // Branch execution never mutates the source.
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("conversion")]
    [InlineData("quality")]
    public void RequiredDataFailuresAreNodeLocalAndDoNotSuppressOtherPaths(string scenario)
    {
        var mapping = Node("bad", "transform.map", """{"rules":[{"target":"required","type":"integer","sourceField":"value","required":true}]}""");
        var snapshot = Snapshot([Input(), mapping, Output("failed"), Output("good")], [new("in", "bad"), new("bad", "failed"), new("in", "good")], "{\"value\":\"text\"}");
        var sample = scenario switch
        {
            "missing" => Message([]),
            "conversion" => Message(new() { ["value"] = FieldValue.FromString("not an integer") }),
            _ => Message([], new() { ["value"] = QualityCode.ReadTimeout }),
        };
        var result = Run(snapshot, sample);
        Assert.Equal(2, result.TotalPaths);
        Assert.Equal(1, result.SucceededPaths);
        var failed = Assert.Single(result.Paths, path => !path.Succeeded);
        Assert.Null(failed.Output);
        Assert.Equal("bad", Assert.Single(failed.Issues).NodeId);
        Assert.Equal("mapping.required", failed.Issues[0].Code);
        var issue = Assert.Single(Assert.Single(failed.Stages).Issues);
        Assert.True(issue.Required);
        Assert.Equal(scenario switch { "missing" => null, "conversion" => QualityCode.ConversionFailed, _ => QualityCode.ReadTimeout }, issue.Reason);
        Assert.NotNull(Assert.Single(result.Paths, path => path.Succeeded).Output);
    }

    [Fact]
    public void OptionalMissingAndBadFieldsStayDistinctAndDoNotInventZeroValues()
    {
        var mapping = Node("map", "transform.map", """{"rules":[{"target":"missing","type":"integer","sourceField":"missing"},{"target":"bad","type":"integer","sourceField":"value"}]}""");
        var snapshot = Snapshot([Input(), mapping, Output()], [new("in", "map"), new("map", "out")], "{\"value\":1,\"missing\":1}");
        var result = Run(snapshot, Message([], new() { ["value"] = QualityCode.Overflow }));
        var path = Assert.Single(result.Paths);
        Assert.True(path.Succeeded);
        var stage = Assert.Single(path.Stages);
        Assert.Equal(2, stage.Issues.Length);
        Assert.Equal("missing", Assert.Single(stage.Fields, field => field.Key == "missing").Quality);
        Assert.Equal("bad", Assert.Single(stage.Fields, field => field.Key == "bad").Quality);
        using var body = JsonDocument.Parse(path.Output!.Body);
        Assert.Empty(body.RootElement.GetProperty("values").EnumerateObject());
        Assert.Equal("overflow", body.RootElement.GetProperty("quality").GetProperty("bad").GetProperty("reason").GetString());
    }

    [Fact]
    public void RepeatedInvalidSchemaPathsCannotPassBecauseOfAFailedValidationCacheEntry()
    {
        var mapping = Node("map", "transform.map", """{"rules":[{"target":"bad","type":"integer","sourceField":"absent"}]}""");
        var snapshot = Snapshot([Input(), mapping, Output("a"), Output("b"), Output("good")],
            [new("in", "map"), new("map", "a"), new("map", "b"), new("in", "good")]);
        var result = Run(snapshot, Message());
        Assert.Equal(1, result.SucceededPaths);
        Assert.Equal(2, result.Paths.Count(path => !path.Succeeded));
        Assert.All(result.Paths.Where(path => !path.Succeeded), path => Assert.Equal("field.missing", Assert.Single(path.Issues).Code));
    }

    [Fact]
    public void InvalidOutputConfigurationAndMissingRequiredPayloadDoNotBlockAnotherOutput()
    {
        var missing = Node("required", "output.http", """{"connectionId":10,"payload":{"mode":"custom","fields":[{"target":"v","sourceField":"value","required":true}]}}""");
        var snapshot = Snapshot([Input(), Output("missing", 99), missing, Output("good")],
            [new("in", "missing"), new("in", "required"), new("in", "good")]);
        var result = Run(snapshot, Message([]));
        Assert.Equal(1, result.SucceededPaths);
        Assert.Contains(result.Paths, path => path.Issues.Any(issue => issue.Code == "output.connectionMissing" && issue.NodeId == "missing"));
        Assert.Contains(result.Paths, path => path.Issues.Any(issue => issue.Code == "output.payload" && issue.NodeId == "required"));
    }

    [Fact]
    public void MqttTopicQosAndCustomPlaceholdersUseFormalSemanticsWithoutBuildingATarget()
    {
        var output = Node("out", "output.mqtt", """{"connectionId":11,"qos":0,"topic":"sample/{deviceKey}","payload":{"mode":"custom","fields":[{"target":"id","placeholder":"{messageId}"},{"target":"source","placeholder":"{sourceMessageId}"},{"target":"large","sourceField":"value"}]}}""");
        var snapshot = Snapshot([Input(), output], [new("in", "out")]);
        snapshot = snapshot with { Connections = new Dictionary<long, ConnectionDraft> { [11] = new(11, "mqtt", "MQTT", ConnectionKind.Mqtt, true, null, new("no-network.invalid"), null) } };
        var sample = Message(new() { ["value"] = FieldValue.FromUInt64(ulong.MaxValue) });
        var result = Run(snapshot, sample);
        var payload = Assert.IsType<RoutePreviewOutput>(Assert.Single(result.Paths).Output);
        Assert.Equal("sample/d1", payload.Address);
        Assert.Equal(0, payload.Qos);
        Assert.Empty(payload.Headers);
        Assert.Contains("18446744073709551615", payload.Body, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(OutputPayloadFormat.Parse(output.Config.GetProperty("payload")).Serialize(sample, payload.DeliveryId)), payload.Body);
    }

    [Fact]
    public void DisplayBudgetCountsEscapedUtf8AcrossAllPathsAndMarksTruncatedBodies()
    {
        var snapshot = Snapshot([Input(), Output("a"), Output("b")], [new("in", "a"), new("in", "b")], "{\"value\":\"text\"}");
        var sample = Message(new() { ["value"] = FieldValue.FromString(string.Concat(Enumerable.Repeat("😀", 12000))) });
        var result = Run(snapshot, sample);
        Assert.Equal(2, result.SucceededPaths); // Display exhaustion does not skip execution.
        Assert.True(result.Truncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, WebJson).Length <= RoutePreviewDisplay.MaximumBytes);
        var payload = Assert.IsType<RoutePreviewOutput>(result.Paths[0].Output);
        Assert.True(payload.Truncated);
        Assert.True(payload.BodyBytes > Encoding.UTF8.GetByteCount(payload.Body));
        Assert.DoesNotContain('\uFFFD', payload.Body);
    }

    [Fact]
    public void OversizedMappingResultFailsOnlyThatPathAndCancellationAndIdentityAreChecked()
    {
        var rules = JsonSerializer.Serialize(new { rules = Enumerable.Range(0, 1000).Select(i => new { target = "f" + i, type = "string", sourceField = "value" }) });
        var snapshot = Snapshot([Input(), Node("map", "transform.map", rules), Output("bad"), Output("good")],
            [new("in", "map"), new("map", "bad"), new("in", "good")], "{\"value\":\"text\"}");
        var result = Run(snapshot, Message(new() { ["value"] = FieldValue.FromString(new string('x', 5000)) }));
        Assert.Equal(1, result.SucceededPaths);
        Assert.Equal("preview.size", Assert.Single(Assert.Single(result.Paths, path => !path.Succeeded).Issues).Code);
        Assert.Throws<ArgumentException>(() => Run(snapshot, Message(device: "wrong")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RouteSamplePreview.Run(snapshot, 1, Message(), cancelled.Token));
    }

    [Fact]
    public void CyclesAreRejectedBeforeExecutingAndOnlySelectedSourcePathsArePreviewed()
    {
        var snapshot = Snapshot([Input("[1,2]"), Output()], [new("in", "out")]);
        snapshot = snapshot with
        {
            Devices = snapshot.Devices.Append(new KeyValuePair<long, DeviceDraft>(2, snapshot.Devices[1] with { Id = 2, Key = "d2" })).ToDictionary(),
            Inputs = snapshot.Inputs.Append(new KeyValuePair<long, InputDraft>(2, snapshot.Inputs[1] with { Id = 2, DeviceId = 2 })).ToDictionary(),
        };
        var result = RouteSamplePreview.Run(snapshot, 2, Message(device: "d2"), Token);
        Assert.Equal(2, result.SourceId);
        Assert.Equal(1, result.TotalPaths);
        var map = Node("map", "transform.map", """{"rules":[{"target":"value","type":"integer","sourceField":"value"}]}""");
        var cycle = Snapshot([Input(), map, Node("other", "transform.map", map.Config.GetRawText()), Output()],
            [new("in", "map"), new("map", "other"), new("other", "map"), new("other", "out")]);
        Assert.Equal("graph.cycle", Assert.Throws<RouteTopologyException>(() => Run(cycle, Message())).Issue.Code);
    }
    [Fact]
    public void DeepStageAndPathMetadataShareOneDisplayBudgetWithoutSkippingExecution()
    {
        var nodes = new List<RouteGraphNode> { Input() };
        var edges = new List<RouteGraphEdge>();
        var previous = "in";
        for (var i = 0; i < 16; i++)
        {
            var id = "map" + i + new string('x', 50);
            nodes.Add(Node(id, "transform.map", """{"rules":[{"target":"value","type":"integer","sourceField":"value"}]}"""));
            edges.Add(new(previous, id));
            previous = id;
        }
        for (var i = 0; i < 32; i++)
        {
            var id = "output" + i + new string('x', 40);
            nodes.Add(Output(id));
            edges.Add(new(previous, id));
        }
        var result = Run(Snapshot(nodes.ToArray(), edges.ToArray()), Message());
        Assert.Equal(32, result.TotalPaths);
        Assert.Equal(32, result.SucceededPaths);
        Assert.True(result.Truncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, WebJson).Length <= RoutePreviewDisplay.MaximumBytes);
    }

    [Fact]
    public void OversizedOutputStillUsesFormalSizeLimitAndDoesNotSuppressOtherPaths()
    {
        var config = JsonSerializer.Serialize(new
        {
            connectionId = 10,
            payload = new
            {
                mode = "custom",
                fields = Enumerable.Range(0, 1000).Select(i => new { target = "f" + i, sourceField = "value" })
            }
        });
        var snapshot = Snapshot([Input(), Node("large", "output.http", config), Output("good")],
            [new("in", "large"), new("in", "good")], "{\"value\":\"text\"}");
        var result = Run(snapshot, Message(new() { ["value"] = FieldValue.FromString(new string('x', 1000)) }));
        Assert.Equal(1, result.SucceededPaths);
        Assert.Equal("output.payload", Assert.Single(Assert.Single(result.Paths, path => !path.Succeeded).Issues).Code);
    }

}

using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class MappedOutputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Inputs = ["meter/sim"];

    [Fact]
    public async Task RequiredFailureRejectsOnlyItsPathWhileOptionalBadRemainsVisible()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [Factory()]);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(),
            Output(1, """[{"target":"scaled","type":"integer","sourceField":"value","scale":2,"offset":1}]"""),
            Output(2, """[{"target":"must","type":"integer","sourceField":"missing","required":true}]"""),
            Output(3, """[{"target":"bad","type":"boolean","sourceField":"value"}]"""),
            Output(4, """[{"target":"optional","type":"number","sourceField":"missing"}]""")));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 3);
        await runtime.StopAsync(Token);
        var first = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Empty(runtime.Deliveries.ReadPending(2, 0, 10));
        using var a = JsonDocument.Parse(first.Payload);
        Assert.Equal(85, a.RootElement.GetProperty("values").GetProperty("scaled").GetInt64());
        var third = Assert.Single(runtime.Deliveries.ReadPending(3, 0, 10));
        using var b = JsonDocument.Parse(third.Payload);
        Assert.Empty(b.RootElement.GetProperty("values").EnumerateObject());
        Assert.True(b.RootElement.GetProperty("quality").TryGetProperty("bad", out _));
        var fourth = Assert.Single(runtime.Deliveries.ReadPending(4, 0, 10));
        using var c = JsonDocument.Parse(fourth.Payload);
        Assert.Empty(c.RootElement.GetProperty("values").EnumerateObject());
        Assert.Empty(c.RootElement.GetProperty("quality").EnumerateObject());
        Assert.Equal(first.SourceMessageId, third.SourceMessageId);
        Assert.Equal(first.SourceMessageId, fourth.SourceMessageId);
        Assert.Equal(1, input.Started);
        var counters = runtime.Deliveries.GetPersistentCounters();
        Assert.Equal(1, counters["path/meter/sim/2/dataRejected"]);
        Assert.Equal(1, counters["path/meter/sim/2/requiredFieldFailures"]);
        Assert.Equal(1, counters["path/meter/sim/3/mappingInvalidFields"]);
        Assert.Equal(1, counters["path/meter/sim/4/mappingMissingFields"]);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task ThirtyTwoMappedPathsShareSerialWorkingSpaceInsteadOfReservingThirtyTwoCopies()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [Factory()]);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan([OutputPipelineTests.Input(), .. Enumerable.Range(1, 32)
            .Select(index => Output(index, """[{"target":"n","type":"integer","sourceField":"value"}]"""))]));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 32);
        await runtime.StopAsync(Token);
        Assert.Equal(1, input.Started);
        foreach (var connection in Enumerable.Range(1, 32))
        { Assert.Single(runtime.Deliveries.ReadPending(connection, 0, 2)); }
        Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 60L * 1024 * 1024, 64L * 1024 * 1024);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task ChangingMappingAfterRestartDoesNotReprocessFrozenRecords()
    {
        using var data = TemporaryDirectory.Create();
        byte[] frozen;
        await using (var first = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [Factory()]))
        {
            await using var plan = first.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(),
                Output(1, """[{"target":"old","type":"integer","sourceField":"value","scale":2}]""")));
            first.StartCore();
            await first.ActivateAsync(plan);
            await OutputPipelineTests.UntilAsync(() => first.Deliveries.GetStatus().CommittedRecords == 1);
            await first.StopAsync(Token);
            frozen = Assert.Single(first.Deliveries.ReadPending(1, 0, 10)).Payload.ToArray();
        }
        await using var second = RuntimeTesting.CreateRuntime([new OneShotInputFactory(0)], data.Path, outputs: [Factory()]);
        await using var next = second.Prepare(new RuntimePlan(2, [OutputPipelineTests.Input(),
            Output(1, """[{"target":"new","type":"integer","constant":999}]""")]));
        second.StartCore();
        await second.ActivateAsync(next);
        await second.StopAsync(Token);
        Assert.Equal(frozen, Assert.Single(second.Deliveries.ReadPending(1, 0, 10)).Payload.ToArray());
        using var document = JsonDocument.Parse(frozen);
        Assert.Equal(84, document.RootElement.GetProperty("values").GetProperty("old").GetInt64());
        Assert.False(document.RootElement.GetProperty("values").TryGetProperty("new", out _));
    }

    [Fact]
    public async Task TwoInputsFeedOneMappedTargetWithoutJoiningTheirSamples()
    {
        using var data = TemporaryDirectory.Create();
        var factory = new OneShotInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path, outputs: [Factory()]);
        var a = new RuntimeUnitPlan(RuntimeUnitId.Create("input.test", "meter/a"), JsonSerializer.SerializeToElement(new { }));
        var b = new RuntimeUnitPlan(RuntimeUnitId.Create("input.test", "meter/b"), JsonSerializer.SerializeToElement(new { }));
        var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "out"), JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            baseAddress = "http://localhost/",
            inputs = new[] { a.Id.Key, b.Id.Key },
            mapping = JsonSerializer.Deserialize<JsonElement>("""[{"target":"n","type":"integer","sourceField":"value","scale":2}]"""),
        }));
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(a, b, output));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
        await runtime.StopAsync(Token);
        var records = runtime.Deliveries.ReadPending(1, 0, 10);
        Assert.Equal(2, records.Count);
        Assert.Equal(2, records.Select(record => record.SourceMessageId).Distinct().Count());
        Assert.Equal(2, factory.Started);
        foreach (var record in records)
        {
            using var document = JsonDocument.Parse(record.Payload);
            Assert.Equal(84, document.RootElement.GetProperty("values").GetProperty("n").GetInt64());
        }
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Theory]
    [InlineData("output.http")]
    [InlineData("output.mqtt")]
    public void InvalidMappingIsRejectedBeforeSnapshotFreezeForBothProtocols(string kind)
    {
        var configuration = new Dictionary<string, object?>
        {
            ["connectionId"] = 1,
            ["inputs"] = Inputs,
            ["mapping"] = new[] { new { target = "bad", type = "number", sourceField = "value", expression = "not-allowed" } },
        };
        if (kind == "output.http")
        { configuration["baseAddress"] = "http://localhost/"; }
        else
        { configuration["host"] = "localhost"; configuration["topic"] = "test"; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units = new[] { new { kind, key = "out", configuration } } });
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(bytes));
    }

    [Theory]
    [InlineData("host", "other.example")]
    [InlineData("port", 1884)]
    [InlineData("tls", true)]
    [InlineData("usePassword", true)]
    [InlineData("timeoutMs", 6000)]
    [InlineData("pingIntervalMs", 20000)]
    [InlineData("clientId", "other-client")]
    public void ConflictingSharedMqttParametersAreRejectedBeforeFreeze(string property, object value)
    {
        var first = new Dictionary<string, object?> { ["connectionId"] = 1, ["host"] = "localhost", ["topic"] = "a", ["inputs"] = Inputs };
        var second = new Dictionary<string, object?>(first) { [property] = value, ["topic"] = "b" };
        var units = new[] { new { kind = "output.mqtt", key = "a", configuration = first }, new { kind = "output.mqtt", key = "b", configuration = second } };
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units })));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public async Task SharedMqttConnectionStillCountsEveryPathAgainstTheFanoutLimit(int count)
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path,
            outputs: [new MqttOutputFactory(new NoMqttCredentials(), TimeProvider.System, new DeniedConnector(), new SharedConnections())]);
        var outputs = Enumerable.Range(1, count).Select(index => new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", "path-" + index),
            JsonSerializer.SerializeToElement(new
            {
                connectionId = 1,
                host = "localhost",
                topic = "path/" + index,
                inputs = Inputs,
                mapping = new[] { new { target = "n", type = "integer", sourceField = "value", scale = index } },
            })));
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan([OutputPipelineTests.Input(), .. outputs]));
        if (count == 33)
        {
            Assert.Single(plan.Failures);
            Assert.Contains("32", plan.Failures[0].Reason, StringComparison.Ordinal);
            return;
        }
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 32);
        Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "output.mqtt");
        await runtime.StopAsync(Token);
        var records = runtime.Deliveries.ReadPending(1, 0, 100);
        Assert.Equal(32, records.Count);
        Assert.Equal(32, records.Select(record => record.DeliveryId).Distinct().Count());
        Assert.Single(records.Select(record => record.SourceMessageId).Distinct());
        foreach (var record in records)
        {
            var index = int.Parse(record.TargetAddress!.AsSpan("path/".Length), System.Globalization.CultureInfo.InvariantCulture);
            using var document = JsonDocument.Parse(record.Payload);
            Assert.Equal(42 * index, document.RootElement.GetProperty("values").GetProperty("n").GetInt64());
        }
        Assert.Equal(1, input.Started);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 1, 64L * 1024 * 1024);
    }

    [Fact]
    public async Task CustomPayloadWorkspaceIsReservedOnceForThirtyTwoSharedHttpPaths()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [Factory()]);
        var outputs = Enumerable.Range(1, 32).Select(index => new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "custom-" + index),
            JsonSerializer.SerializeToElement(new
            {
                connectionId = 1,
                baseAddress = "http://localhost/",
                path = "events",
                inputs = Inputs,
                payload = new { mode = "custom", fields = new[] { new { target = "fixed", constant = new string('x', 1000) } } },
            })));
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan([OutputPipelineTests.Input(), .. outputs]));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 32);
        await runtime.StopAsync(Token);
        Assert.Equal(32, runtime.Deliveries.ReadPending(1, 0, 100).Count);
        Assert.InRange(runtime.InFlight.Status.PeakReservedBytes, 32L * 1024 * 1024, 64L * 1024 * 1024);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Theory]
    [InlineData("output.http")]
    [InlineData("output.mqtt")]
    public void InvalidPayloadFormatIsRejectedBeforeSnapshotFreeze(string kind)
    {
        var configuration = new Dictionary<string, object?>
        {
            ["connectionId"] = 1,
            ["inputs"] = Inputs,
            ["payload"] = new { mode = "custom", fields = new[] { new { target = "bad", placeholder = "{script}" } } },
        };
        if (kind == "output.http")
        { configuration["baseAddress"] = "http://localhost/"; }
        else
        { configuration["host"] = "localhost"; configuration["topic"] = "test"; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units = new[] { new { kind, key = "out", configuration } } });
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(bytes));
    }

    [Fact]
    public async Task CustomRequiredFailureDoesNotRejectAnotherFormatOnSameConnection()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [Factory()]);
        RuntimeUnitPlan Path(string key, string payload) => new(RuntimeUnitId.Create("output.http", key),
            JsonSerializer.SerializeToElement(new
            {
                connectionId = 1,
                baseAddress = "http://localhost/",
                path = "events",
                inputs = Inputs,
                payload = JsonSerializer.Deserialize<JsonElement>(payload),
            }));
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(),
            Path("a", """{"mode":"custom","fields":[{"target":"n","sourceField":"missing","required":true}]}"""),
            Path("b", """{"mode":"values"}""")));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 1);
        await runtime.StopAsync(Token);
        var record = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        using var json = JsonDocument.Parse(record.Payload);
        Assert.Equal(42, json.RootElement.GetProperty("value").GetInt64());
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["route/meter/sim/output.http/a/dataRejected"]);
        Assert.Equal(1, input.Started);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    private sealed class NoMqttCredentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
    }

    private static RuntimeUnitPlan Output(int connectionId, string rules) => new(RuntimeUnitId.Create("output.http", $"out-{connectionId}"),
        JsonSerializer.SerializeToElement(new
        {
            connectionId,
            baseAddress = "http://localhost/",
            path = "events",
            inputs = Inputs,
            mapping = JsonSerializer.Deserialize<JsonElement>(rules)
        }));
    private static HttpOutputFactory Factory() => new(new Anonymous(), TimeProvider.System, new DeniedConnector(), new SharedConnections());
    private sealed class Anonymous : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private sealed class DeniedConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new OutboundAccessDeniedException();
    }
}

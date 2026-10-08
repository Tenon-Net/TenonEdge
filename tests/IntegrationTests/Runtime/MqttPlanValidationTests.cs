using System.Text.Json;
using System.Text.Json.Nodes;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class MqttPlanValidationTests
{
    [Theory]
    [InlineData("values", "values")]
    [InlineData("device/+", "device/value")]
    [InlineData("device/#", "device")]
    [InlineData("#", "device/value")]
    [InlineData("device/#", "{deviceKey}/value")]
    public void KnownSelfFeedbackIsRejected(string filter, string topic)
    {
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(
            Input("device/one", "broker", filter, 1), Output("out", "BROKER", topic, 1, "device/one")));
    }

    [Theory]
    [InlineData("in/#", "out/value")]
    [InlineData("#", "$SYS/value")]
    [InlineData("in/+", "in/nested/value")]
    public void NonMatchingTopicsDoNotCreateAFeedbackEdge(string filter, string topic)
    {
        Assert.Equal(2, Parse(Input("device/one", "broker", filter, 1),
            Output("out", "broker", topic, 1, "device/one")).Units.Count);
    }

    [Fact]
    public void TwoBrokerCycleIsRejectedButOneWayForwardingIsAllowed()
    {
        var one = Input("device/one", "alpha", "in/one", 1);
        var two = Input("device/two", "beta", "in/two", 2);
        var forward = Output("forward", "beta", "in/two", 2, "device/one");
        Assert.Equal(3, Parse(one, two, forward).Units.Count);
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(one, two, forward,
            Output("return", "alpha", "in/one", 1, "device/two")));
    }

    [Fact]
    public void InputOutputConnectionParametersAreCheckedBeforeFreeze()
    {
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(Input("device/one", "alpha", "in", 1),
            Output("out", "beta", "out", 1, "device/one")));
        var output = Output("out", "alpha", "out", 1, "device/one");
        output["configuration"]!["timeoutMs"] = 1000;
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(Input("device/one", "alpha", "in", 1), output));
    }

    [Theory]
    [InlineData("password", "secret")]
    [InlineData("unknown", "bad")]
    public void InputSecretsAndUnknownPropertiesAreRejectedBeforeFreeze(string name, string value)
    {
        var input = Input("device/one", "alpha", "in", 1);
        input["configuration"]![name] = value;
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(input));
    }

    [Fact]
    public void RetainedInputRequiresAValidCompiledTimeMapping()
    {
        var input = Input("device/one", "alpha", "in", 1);
        input["configuration"]!["processRetained"] = true;
        input["configuration"]!["upstreamTime"] = JsonNode.Parse("""{"path":"/at","format":"unknown"}""");
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(input));
        input["configuration"]!["upstreamTime"]!["format"] = "rfc3339";
        Assert.Single(Parse(input).Units);
    }

    [Fact]
    public void TwoInputConnectionsCannotKickEachOtherOffWithTheSameExplicitClientId()
    {
        var one = Input("device/one", "alpha", "in/one", 1);
        var two = Input("device/two", "ALPHA", "in/two", 2);
        one["configuration"]!["clientId"] = "same-client";
        two["configuration"]!["clientId"] = "same-client";
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(one, two));
        two["configuration"]!["host"] = "beta";
        Assert.Equal(2, Parse(one, two).Units.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualDefaultClientIdCannotCollideWithExplicitInputOrOutputClientId(bool secondIsOutput)
    {
        using var data = TemporaryDirectory.Create();
        var nodeId = Guid.NewGuid();
        var one = Input("device/one", "alpha", "in/one", 1);
        var two = secondIsOutput ? Output("out", "alpha", "out/value", 2, "device/one")
            : Input("device/two", "alpha", "in/two", 2);
        two["configuration"]!["clientId"] = "tenon-" + nodeId.ToString("N") + "-1";
        var document = Parse(one, two); // 文本校验无法获知宿主 NodeId；运行时展开后再拒绝。
        var connector = new NoNetwork();
        var provider = new MqttPlanAdapterProvider(new AnonymousCredentials(), new JsonInputDecoderFactory(), new SharedConnections(),
            connector, TimeProvider.System, nodeId.ToString(), (_, _) => { });
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, planAdapters: [provider]);
        await using var prepared = runtime.Prepare(document.ToPlan(1));
        Assert.Single(prepared.Failures);
        Assert.Equal(secondIsOutput ? "output.mqtt" : "input.mqtt", prepared.Failures[0].Id.Kind);
        Assert.Equal(0, connector.Attempts);
    }

    private sealed class AnonymousCredentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
    }
    private sealed class NoNetwork : IOutboundConnector
    {
        public int Attempts { get; private set; }
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("Prepare must not connect.");
        }
    }

    private static JsonObject Input(string key, string host, string filter, int connectionId) => new()
    {
        ["kind"] = "input.mqtt",
        ["key"] = key,
        ["configuration"] = new JsonObject
        {
            ["connectionId"] = connectionId,
            ["host"] = host,
            ["topicFilter"] = filter,
            ["fields"] = JsonNode.Parse("""[{"key":"v","path":"/v","type":"integer"}]"""),
        },
    };
    private static JsonObject Output(string key, string host, string topic, int connectionId, string source) => new()
    {
        ["kind"] = "output.mqtt",
        ["key"] = key,
        ["configuration"] = new JsonObject
        { ["connectionId"] = connectionId, ["host"] = host, ["topic"] = topic, ["inputs"] = new JsonArray(source) },
    };
    private static RuntimePlanDocument Parse(params JsonObject[] units) =>
        RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units }));
}

using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Adapters;

public sealed class QueueOnlyOutputTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    public async Task QueueOnlyIsExplicitHasNoInputsAndCannotGenerateNewDeliveries(string protocol)
    {
        var factory = Factory(protocol);
        var target = factory.Build(new(RuntimeUnitId.Create(factory.Kind, "queue/target/sender"), Configuration(protocol, "{}")));
        Assert.Empty(target.Inputs);
        Assert.Equal(protocol, target.CaptureIdentity()!.Recipient.Protocol);
        var at = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "sample", "meter", at, TimeSource.GatewayUnsynced, at,
            new Dictionary<string, FieldValue> { ["n"] = FieldValue.FromInt64(42) });
        Assert.Throws<InvalidOperationException>(() => target.CreateMessage(message, Guid.NewGuid()));
        Assert.Throws<RuntimeUnitBuildException>(() => factory.Build(new(target.Id, Configuration(protocol, """{"queueOnly":false}"""))));
        // 构建和释放不访问网络，原发送适配器仍负责冻结记录和出站校验。
        await using var adapter = target.CreateAdapter();
    }

    [Theory]
    [InlineData("http", "{\"inputs\":[\"meter/sample\"]}")]
    [InlineData("http", "{\"inputs\":null}")]
    [InlineData("http", "{\"path\":\"new\"}")]
    [InlineData("http", "{\"headers\":{\"X-Test\":\"new\"}}")]
    [InlineData("http", "{\"successPath\":\"/ok\",\"successValue\":true}")]
    [InlineData("http", "{\"mapping\":null}")]
    [InlineData("http", "{\"mappingStages\":[]}")]
    [InlineData("http", "{\"payload\":{\"mode\":\"values\"}}")]
    [InlineData("mqtt", "{\"inputs\":[\"meter/sample\"]}")]
    [InlineData("mqtt", "{\"inputs\":null}")]
    [InlineData("mqtt", "{\"topic\":\"new\"}")]
    [InlineData("mqtt", "{\"topic\":null}")]
    [InlineData("mqtt", "{\"qos\":0}")]
    [InlineData("mqtt", "{\"mapping\":null}")]
    [InlineData("mqtt", "{\"mappingStages\":[]}")]
    [InlineData("mqtt", "{\"payload\":{\"mode\":\"values\"}}")]
    public void QueueOnlyRejectsUnusedFormattingSettingsRatherThanPretendingTheyAffectFrozenRecords(string protocol, string patch)
    {
        var factory = Factory(protocol);
        Assert.Throws<RuntimeUnitBuildException>(() => factory.Build(new(RuntimeUnitId.Create(factory.Kind, "queue/target/sender"), Configuration(protocol, patch))));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    public void QueueOnlyCanShareOneSenderWithNormalPathsOnTheSamePhysicalConnection(string protocol)
    {
        var factory = Factory(protocol);
        var queue = factory.Build(new(RuntimeUnitId.Create(factory.Kind, "queue/target/sender"), Configuration(protocol, "{}")));
        var route = factory.Build(new(RuntimeUnitId.Create(factory.Kind, "route/out"), Configuration(protocol, protocol == "http"
            ? """{"queueOnly":false,"inputs":["meter/sample"],"path":"frozen"}"""
            : """{"queueOnly":false,"inputs":["meter/sample"],"topic":"frozen","qos":0}""")));
        Assert.True(queue.CanShareSenderWith(route));
        Assert.True(route.CanShareSenderWith(queue));
        Assert.Equal(queue.CurrentFingerprint, route.CurrentFingerprint);
    }

    [Fact]
    public void MissingLegacyCredentialReferenceFailsOnlyThatSenderBeforeAnyCredentialOrNetworkAccess()
    {
        var factory = Factory("mqtt");
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create(factory.Kind, "queue/legacy/sender"),
            Configuration("mqtt", """{"queueCredentialReferenceMissing":true}"""));
        var error = Assert.Throws<RuntimeUnitBuildException>(() => factory.Build(unit));
        Assert.Contains("凭证引用", error.Message, StringComparison.Ordinal);
    }

    private static IOutputAdapterFactory Factory(string protocol) => protocol == "http"
        ? new HttpOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new SharedConnections())
        : new MqttOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new SharedConnections());

    private static JsonElement Configuration(string protocol, string patch)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(protocol == "http"
            ? """{"connectionId":1,"queueOnly":true,"baseAddress":"https://server.test/api/"}"""
            : """{"connectionId":1,"queueOnly":true,"host":"broker.test"}""")!;
        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(patch)!)
        { values[key] = value; }
        return JsonSerializer.SerializeToElement(values);
    }

    private sealed class Credentials : ICredentialResolver, IHttpCredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private sealed class NoNetwork : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new InvalidOperationException("No network during prepare.");
    }
}

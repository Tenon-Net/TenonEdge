using System.Buffers;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 MQTT 最小原型（V-04 的 T0 部分，验证代码，不是正式适配器）：显式 MQTT 3.1.1 连接真实 Mosquitto，Broker 拒绝匿名与错误密码；
/// QoS 1 发布得到 PUBACK，另一个独立的订阅者逐字节收到已知内容；QoS 0 同样送达。
/// </summary>
[Trait("Category", "RealBroker")]
public sealed class MqttPublishProbeTests(MosquittoBrokerFixture fixture, ITestOutputHelper output) : IClassFixture<MosquittoBrokerFixture>
{
    [Fact]
    public async Task AnExplicitMqtt311ConnectionWithCredentialsIsAccepted()
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (client, result) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), cancellationToken);
        using (client)
        {
            Assert.Equal(MqttProtocolVersion.V311, client.Options.ProtocolVersion);
            Assert.True(client.IsConnected);
            Assert.False(result.IsSessionPresent);
            await MqttProbe.DisconnectAsync(client, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false, MosquittoBroker.Password)]
    [InlineData(true, "wrong-password")]
    public async Task AnonymousAndWrongPasswordConnectionsAreRefused(bool authenticate, string password)
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new MqttClientFactory().CreateMqttClient();
        using var timeout = MqttProbe.Timeout(cancellationToken);

        // Broker 以 CONNACK 拒绝时，ConnectAsync 返回带结果码的结果，不抛异常。
        var result = await client.ConnectAsync(MqttProbe.Options(broker, NewClientId(), authenticate: authenticate, password: password), timeout.Token);
        output.WriteLine($"认证 {authenticate}，密码 {(password == MosquittoBroker.Password ? "正确" : "错误")}：{result.ResultCode}");

        // Mosquitto 对 3.1.1 的匿名与错误密码都回 CONNACK 返回码 5（未授权）。
        Assert.Equal(MqttClientConnectResultCode.NotAuthorized, result.ResultCode);
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData(MqttQualityOfServiceLevel.AtMostOnce)]
    public async Task AnIndependentSubscriberReceivesTheExactPayload(MqttQualityOfServiceLevel qualityOfService)
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;
        var topic = MqttProbe.NewTopic("publish");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), cancellationToken);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), cancellationToken);
        using (subscriber)
        using (publisher)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, cancellationToken);

            // 已知内容：超过 2^53 的整数、中文，以及 0x00、0xFF 这类非文本字节。
            byte[] payload = [.. """{"value":9007199254740993,"name":"温度"}"""u8, 0x00, 0xFF];
            using var publishTimeout = MqttProbe.Timeout(cancellationToken, TimeSpan.FromSeconds(5));
            var result = await MqttProbe.PublishAsync(publisher, topic, payload, qualityOfService, publishTimeout.Token);
            var message = await received.NextAsync(cancellationToken);
            output.WriteLine($"{qualityOfService}：IsSuccess {result.IsSuccess}，ReasonCode {result.ReasonCode}，PacketIdentifier {result.PacketIdentifier}");

            // 3.1.1 的 PUBACK 没有原因码，成功即为 Success；QoS 0 只表示本地写出完成。
            Assert.True(result.IsSuccess);
            Assert.Equal(MqttClientPublishReasonCode.Success, result.ReasonCode);
            Assert.Equal(payload, message.ApplicationMessage.Payload.ToArray());
            Assert.Equal(topic, message.ApplicationMessage.Topic);
            Assert.Equal(qualityOfService, message.ApplicationMessage.QualityOfServiceLevel);
            Assert.False(message.ApplicationMessage.Retain);
            Assert.False(message.ApplicationMessage.Dup);
        }
    }

    private static string NewClientId() => $"probe-{Guid.NewGuid():N}";
}

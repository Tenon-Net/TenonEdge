using MQTTnet;
using MQTTnet.Protocol;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 持久会话与手动确认的最小探针（V-04，验证代码，不是正式适配器；完整的输入背压、会话恢复与保留消息在 T2-03）：
/// <list type="bullet">
/// <item>3.1.1、CleanSession=false、固定 ClientId：断开期间发布的 QoS 1 消息在重连后送达，SessionPresent=1，不需要重新订阅。</item>
/// <item>以 CleanSession=true 连接一次会清除会话，之后重连 SessionPresent=0、订阅也不在了：这是设计 §6.2 “会话丢失”须重新订阅的情形。</item>
/// <item>关闭自动确认后不确认的消息，在重连后被重投且 DUP=1；同一条消息确认两次抛异常。</item>
/// <item>不确认时 Broker 只投递到在途窗口（Mosquitto 默认 max_inflight_messages 20），确认之后才继续：QoS 1 输入的背压由此形成。</item>
/// </list>
/// </summary>
[Trait("Category", "RealBroker")]
public sealed class MqttSessionProbeTests(MosquittoBrokerFixture fixture, ITestOutputHelper output) : IClassFixture<MosquittoBrokerFixture>
{
    [Fact]
    public async Task APersistentSessionDeliversMessagesPublishedWhileOffline()
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;
        var clientId = NewClientId();
        var topic = MqttProbe.NewTopic("session");

        var (first, firstResult) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), cancellationToken);
        using (first)
        {
            Assert.False(firstResult.IsSessionPresent);
            await MqttProbe.SubscribeAsync(first, topic, cancellationToken);
            await MqttProbe.DisconnectAsync(first, cancellationToken);
        }

        await PublishAsync(broker, topic, ["m0", "m1", "m2"], cancellationToken);

        using var resumed = new MqttClientFactory().CreateMqttClient();
        var received = ReceivedMessages.Attach(resumed);
        using var timeout = MqttProbe.Timeout(cancellationToken);
        var result = await resumed.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), timeout.Token);

        Assert.True(result.IsSessionPresent);
        foreach (var expected in new[] { "m0", "m1", "m2" })
        {
            Assert.Equal(expected, (await received.NextAsync(cancellationToken)).ApplicationMessage.ConvertPayloadToString());
        }
    }

    [Fact]
    public async Task ACleanSessionConnectionDiscardsTheStoredSessionAndItsSubscriptions()
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;
        var clientId = NewClientId();
        var topic = MqttProbe.NewTopic("session-lost");

        var (first, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), cancellationToken);
        using (first)
        {
            await MqttProbe.SubscribeAsync(first, topic, cancellationToken);
            await MqttProbe.DisconnectAsync(first, cancellationToken);
        }

        var (clean, cleanResult) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: true), cancellationToken);
        using (clean)
        {
            Assert.False(cleanResult.IsSessionPresent);
            await MqttProbe.DisconnectAsync(clean, cancellationToken);
        }

        using var resumed = new MqttClientFactory().CreateMqttClient();
        var received = ReceivedMessages.Attach(resumed);
        using var timeout = MqttProbe.Timeout(cancellationToken);
        var result = await resumed.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), timeout.Token);
        await PublishAsync(broker, topic, ["after"], cancellationToken);

        // SessionPresent=0：原来的订阅已不存在，须重新订阅，断开期间的消息可能已丢失。
        Assert.False(result.IsSessionPresent);
        Assert.Empty(await received.CollectAsync(TimeSpan.FromSeconds(1), cancellationToken));
    }

    [Fact]
    public async Task AnUnacknowledgedMessageIsRedeliveredWithDupAfterReconnecting()
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;
        var clientId = NewClientId();
        var topic = MqttProbe.NewTopic("redelivery");

        var (first, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), cancellationToken);
        using (first)
        {
            var received = ReceivedMessages.Attach(first, autoAcknowledge: false);
            await MqttProbe.SubscribeAsync(first, topic, cancellationToken);
            await PublishAsync(broker, topic, ["pending"], cancellationToken);
            var original = await received.NextAsync(cancellationToken);
            Assert.False(original.ApplicationMessage.Dup);

            // 不确认就断开。
            await MqttProbe.DisconnectAsync(first, cancellationToken);
        }

        using var second = new MqttClientFactory().CreateMqttClient();
        var redelivered = ReceivedMessages.Attach(second, autoAcknowledge: false);
        using (var timeout = MqttProbe.Timeout(cancellationToken))
        {
            Assert.True((await second.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), timeout.Token)).IsSessionPresent);
        }

        var again = await redelivered.NextAsync(cancellationToken);
        output.WriteLine($"重投：{again.ApplicationMessage.ConvertPayloadToString()}，DUP={again.ApplicationMessage.Dup}，报文编号 {again.PacketIdentifier}");
        Assert.Equal("pending", again.ApplicationMessage.ConvertPayloadToString());
        Assert.True(again.ApplicationMessage.Dup);

        await again.AcknowledgeAsync(cancellationToken);
        var twice = await Record.ExceptionAsync(() => again.AcknowledgeAsync(cancellationToken));
        output.WriteLine($"第二次确认：{MqttProbe.Describe(twice)}");
        Assert.IsType<InvalidOperationException>(twice);
        await MqttProbe.DisconnectAsync(second, cancellationToken);

        // 已确认的消息不再重投。
        using var third = new MqttClientFactory().CreateMqttClient();
        var afterAcknowledgement = ReceivedMessages.Attach(third);
        using (var timeout = MqttProbe.Timeout(cancellationToken))
        {
            Assert.True((await third.ConnectAsync(MqttProbe.Options(broker, clientId, cleanSession: false), timeout.Token)).IsSessionPresent);
        }

        Assert.Empty(await afterAcknowledgement.CollectAsync(TimeSpan.FromSeconds(1), cancellationToken));
    }

    [Fact]
    public async Task TheBrokerStopsAtItsInflightWindowUntilMessagesAreAcknowledged()
    {
        var broker = fixture.Require();
        var cancellationToken = TestContext.Current.CancellationToken;
        var topic = MqttProbe.NewTopic("inflight");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId(), cleanSession: false), cancellationToken);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber, autoAcknowledge: false);
            await MqttProbe.SubscribeAsync(subscriber, topic, cancellationToken);
            await PublishAsync(broker, topic, [.. Enumerable.Range(0, 30).Select(index => $"m{index}")], cancellationToken);

            var window = await received.CollectAsync(TimeSpan.FromSeconds(2), cancellationToken);
            output.WriteLine($"不确认时收到 {window.Count} 条");
            Assert.Equal(20, window.Count);
            Assert.Equal(Enumerable.Range(0, 20).Select(index => $"m{index}"), window.Select(message => message.ApplicationMessage.ConvertPayloadToString()));

            // 按到达顺序确认之后，Broker 继续投递其余消息。
            foreach (var message in window)
            {
                await message.AcknowledgeAsync(cancellationToken);
            }

            var rest = await received.CollectAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(Enumerable.Range(20, 10).Select(index => $"m{index}"), rest.Select(message => message.ApplicationMessage.ConvertPayloadToString()));
            foreach (var message in rest)
            {
                await message.AcknowledgeAsync(cancellationToken);
            }
        }
    }

    private static async Task PublishAsync(MosquittoBroker broker, string topic, IReadOnlyList<string> payloads, CancellationToken cancellationToken)
    {
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), cancellationToken);
        using (publisher)
        {
            foreach (var payload in payloads)
            {
                using var timeout = MqttProbe.Timeout(cancellationToken, TimeSpan.FromSeconds(5));
                var result = await MqttProbe.PublishAsync(publisher, topic, System.Text.Encoding.UTF8.GetBytes(payload), MqttQualityOfServiceLevel.AtLeastOnce, timeout.Token);
                Assert.True(result.IsSuccess);
            }

            await MqttProbe.DisconnectAsync(publisher, cancellationToken);
        }
    }

    private static string NewClientId() => $"probe-{Guid.NewGuid():N}";
}

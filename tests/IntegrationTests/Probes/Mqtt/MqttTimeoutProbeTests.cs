using System.Buffers;
using System.Diagnostics;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 MQTT 最小原型（V-04 中与发布有关的结论，验证代码，不是正式适配器）：发布超时与断线检测。每个用例独占一个 Broker。
/// <list type="bullet">
/// <item>Broker 暂停响应时，QoS 1 的 PublishAsync 由调用方的取消令牌按时结束（MqttCommunicationTimedOutException）；
/// 恢复后迟到的 PUBACK 不会断开连接（5.2.0 的修复），但这条“超时”的消息其实已送达，重发会造成重复。</item>
/// <item>持续发布时拔掉网线（用测试代理模拟）：每次发送都刷新“最后发送时间”，保活不再发 PINGREQ，客户端一直认为连接正常（issue #2244）；
/// 只有设计 §2.3 的做法——发布连续超时后由网关主动断开重连——能恢复。空闲时保活能在约 1.5 个周期内发现断线。</item>
/// <item>QoS 0 持续发布时既不超时也不报错，上面的做法不适用；由调用方定期发送带超时的 PINGREQ（PingAsync）可以发现断线。</item>
/// <item>测试代理在本机替客户端确认数据，所以模拟不出真实拔线时内核 TCP 重传超时（Linux 默认约 15 分钟）最终使套接字报错的情形；
/// 结论只说明在观察窗内库没有发现断线。</item>
/// </list>
/// </summary>
[Trait("Category", "RealBroker")]
public sealed class MqttTimeoutProbeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShortKeepAlive = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);
    private static readonly byte[] Payload = """{"value":1}"""u8.ToArray();

    [Fact]
    public async Task APublishEndsByItsTokenWhileTheBrokerIsPausedAndTheLatePubAckDoesNotDisconnect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(cancellationToken);
        var topic = MqttProbe.NewTopic("paused");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId(), keepAlive: TimeSpan.FromMinutes(1)), cancellationToken);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId(), keepAlive: TimeSpan.FromMinutes(1)), cancellationToken);
        using (subscriber)
        using (publisher)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, cancellationToken);
            var disconnections = 0;
            publisher.DisconnectedAsync += _ =>
            {
                Interlocked.Increment(ref disconnections);
                return Task.CompletedTask;
            };

            await broker.PauseAsync(cancellationToken);
            (Exception? Exception, TimeSpan Elapsed) timedOut;
            try
            {
                using var publishTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout);
                timedOut = await MqttProbe.MeasureAsync(() => MqttProbe.PublishAsync(publisher, topic, Payload, MqttQualityOfServiceLevel.AtLeastOnce, publishTimeout.Token));
            }
            finally
            {
                await broker.UnpauseAsync(cancellationToken);
            }

            output.WriteLine($"Broker 暂停时发布：{timedOut.Elapsed.TotalMilliseconds:F0} ms，{MqttProbe.Describe(timedOut.Exception)}");
            Assert.IsType<MqttCommunicationTimedOutException>(timedOut.Exception);
            Assert.InRange(timedOut.Elapsed, PublishTimeout - TimeSpan.FromMilliseconds(50), PublishTimeout + Slack);

            // 恢复后：已写出的 PUBLISH 被 Broker 处理并转发，迟到的 PUBACK 找不到等待者，只记警告，不断开。
            var late = await received.NextAsync(cancellationToken);
            Assert.Equal(Payload, late.ApplicationMessage.Payload.ToArray());
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            Assert.Equal(0, Volatile.Read(ref disconnections));
            Assert.True(publisher.IsConnected);

            using var nextTimeout = MqttProbe.Timeout(cancellationToken, TimeSpan.FromSeconds(5));
            Assert.True((await MqttProbe.PublishAsync(publisher, topic, Payload, MqttQualityOfServiceLevel.AtLeastOnce, nextTimeout.Token)).IsSuccess);
        }
    }

    [Fact]
    public async Task ContinuousQoS1PublishingKeepsKeepAliveFromNoticingALostConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(cancellationToken);
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        var clientId = NewClientId();
        var topic = MqttProbe.NewTopic("cable");
        var (client, _) = await MqttProbe.ConnectAsync(ThroughProxy(proxy, clientId), cancellationToken);
        using (client)
        {
            var lost = WatchDisconnection(client, out var stopwatch);
            using (var firstTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout))
            {
                Assert.True((await MqttProbe.PublishAsync(client, topic, Payload, MqttQualityOfServiceLevel.AtLeastOnce, firstTimeout.Token)).IsSuccess);
            }

            proxy.Freeze();
            stopwatch.Restart();
            var observation = ShortKeepAlive * 5;
            var timeouts = 0;
            Exception? other = null;
            while (stopwatch.Elapsed < observation && !lost.Task.IsCompleted && other is null)
            {
                using var publishTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout);
                var exception = await Record.ExceptionAsync(() => MqttProbe.PublishAsync(client, topic, Payload, MqttQualityOfServiceLevel.AtLeastOnce, publishTimeout.Token));
                if (exception is MqttCommunicationTimedOutException)
                {
                    timeouts++;
                }
                else
                {
                    other = exception;
                }
            }

            TimeSpan? detected = lost.Task.IsCompleted ? await lost.Task : null;
            output.WriteLine($"持续 QoS 1 发布、拔网线 {stopwatch.Elapsed.TotalSeconds:F1} 秒：发布超时 {timeouts} 次，其他结果 {MqttProbe.Describe(other)}，"
                + $"断线{(detected is { } after ? $"在 {after.TotalSeconds:F1} 秒被发现" : "没有被发现")}，IsConnected={client.IsConnected}");

            // 已记录的库行为（#2244）：保活周期 2 秒，5 个周期内都没有发现断线，客户端仍认为已连接。
            Assert.False(lost.Task.IsCompleted);
            Assert.True(client.IsConnected);
            Assert.Null(other);
            Assert.True(timeouts >= 5, $"只有 {timeouts} 次超时。");

            // 设计 §2.3 的做法：发布连续超时后由网关主动断开；网络恢复后重新连接并继续发布。
            using (var disconnectTimeout = MqttProbe.Timeout(cancellationToken, TimeSpan.FromSeconds(2)))
            {
                await Record.ExceptionAsync(() => client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), disconnectTimeout.Token));
            }

            Assert.False(client.IsConnected);
            proxy.Thaw();
            var (reconnected, _) = await MqttProbe.ConnectAsync(ThroughProxy(proxy, clientId), cancellationToken);
            using (reconnected)
            {
                using var publishTimeout = MqttProbe.Timeout(cancellationToken, TimeSpan.FromSeconds(5));
                Assert.True((await MqttProbe.PublishAsync(reconnected, topic, Payload, MqttQualityOfServiceLevel.AtLeastOnce, publishTimeout.Token)).IsSuccess);
            }
        }
    }

    [Fact]
    public async Task ContinuousQoS0PublishingNeverNoticesALostConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(cancellationToken);
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        var (client, _) = await MqttProbe.ConnectAsync(ThroughProxy(proxy, NewClientId()), cancellationToken);
        using (client)
        {
            var lost = WatchDisconnection(client, out var stopwatch);
            var topic = MqttProbe.NewTopic("cable-qos0");
            proxy.Freeze();
            stopwatch.Restart();
            var failures = 0;
            var published = 0;
            while (stopwatch.Elapsed < ShortKeepAlive * 4)
            {
                using var publishTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout);
                if (await Record.ExceptionAsync(() => MqttProbe.PublishAsync(client, topic, Payload, MqttQualityOfServiceLevel.AtMostOnce, publishTimeout.Token)) is null)
                {
                    published++;
                }
                else
                {
                    failures++;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            output.WriteLine($"持续 QoS 0 发布、拔网线 {stopwatch.Elapsed.TotalSeconds:F1} 秒：{published} 次“成功”，{failures} 次失败，断线{(lost.Task.IsCompleted ? "被发现" : "没有被发现")}");

            // 已记录的库行为：QoS 0 写进发送缓冲即算成功，持续发布时断线既不报错也不被保活发现。
            Assert.Equal(0, failures);
            Assert.False(lost.Task.IsCompleted);
        }
    }

    [Fact]
    public async Task AnExplicitPingNoticesALostConnectionWhileQoS0PublishingContinues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(cancellationToken);
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        var (client, _) = await MqttProbe.ConnectAsync(ThroughProxy(proxy, NewClientId()), cancellationToken);
        using (client)
        {
            var lost = WatchDisconnection(client, out var stopwatch);
            var topic = MqttProbe.NewTopic("ping-qos0");
            using (var pingTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout))
            {
                await client.PingAsync(pingTimeout.Token);
            }

            proxy.Freeze();
            stopwatch.Restart();
            Exception? pingFailure = null;
            var published = 0;
            while (pingFailure is null && stopwatch.Elapsed < ShortKeepAlive * 4)
            {
                // QoS 0 照常写出，期间每轮发一次带 1 秒超时的 PINGREQ。
                for (var i = 0; i < 5; i++)
                {
                    using var publishTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout);
                    await MqttProbe.PublishAsync(client, topic, Payload, MqttQualityOfServiceLevel.AtMostOnce, publishTimeout.Token);
                    published++;
                }

                using var pingTimeout = MqttProbe.Timeout(cancellationToken, PublishTimeout);
                pingFailure = await Record.ExceptionAsync(() => client.PingAsync(pingTimeout.Token));
            }

            var detectedAfter = stopwatch.Elapsed;
            output.WriteLine($"QoS 0 持续发布中拔网线：{detectedAfter.TotalSeconds:F1} 秒后 PINGREQ 超时（{MqttProbe.Describe(pingFailure)}），"
                + $"期间写出 {published} 条，库自己{(lost.Task.IsCompleted ? "已" : "未")}发现断线");

            Assert.IsType<MqttCommunicationTimedOutException>(pingFailure);
            Assert.InRange(detectedAfter, PublishTimeout - TimeSpan.FromMilliseconds(50), PublishTimeout + Slack);
        }
    }

    [Fact]
    public async Task KeepAliveNoticesALostConnectionWhenTheClientIsIdle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(cancellationToken);
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        var (client, _) = await MqttProbe.ConnectAsync(ThroughProxy(proxy, NewClientId()), cancellationToken);
        using (client)
        {
            var lost = WatchDisconnection(client, out var stopwatch);
            proxy.Freeze();
            stopwatch.Restart();

            var detectedAfter = await lost.Task.WaitAsync(ShortKeepAlive * 5, cancellationToken);
            output.WriteLine($"空闲时拔网线：{detectedAfter.TotalSeconds:F1} 秒后发现断线（保活周期 {ShortKeepAlive.TotalSeconds} 秒）");

            // 距上次发送满一个周期才发 PINGREQ，再等半个周期的 PINGRESP。
            Assert.InRange(detectedAfter, ShortKeepAlive, (ShortKeepAlive * 2) + Slack);
            Assert.False(client.IsConnected);
        }
    }

    private static MqttClientOptions ThroughProxy(TcpFaultProxy proxy, string clientId) =>
        MqttProbe.Options(MosquittoBroker.ServerName, proxy.Port, clientId, keepAlive: ShortKeepAlive);

    private static TaskCompletionSource<TimeSpan> WatchDisconnection(IMqttClient client, out Stopwatch stopwatch)
    {
        var lost = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        client.DisconnectedAsync += _ =>
        {
            lost.TrySetResult(clock.Elapsed);
            return Task.CompletedTask;
        };
        stopwatch = clock;
        return lost;
    }

    private static string NewClientId() => $"probe-{Guid.NewGuid():N}";
}

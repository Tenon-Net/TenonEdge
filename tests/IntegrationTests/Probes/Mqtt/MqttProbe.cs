using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 的 MQTTnet 用法（验证代码，不是正式适配器）：显式 MQTT 3.1.1、固定 ClientId、显式 CleanSession 与保活，按需加 TLS。
/// 发布、订阅、连接都传入带超时的取消令牌：PublishAsync 只受取消令牌控制（S-02），不传令牌时 Connect/Subscribe 才用选项中的超时。
/// </summary>
internal static class MqttProbe
{
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public static MqttClientOptions Options(
        MosquittoBroker broker,
        string clientId,
        bool cleanSession = true,
        TimeSpan? keepAlive = null,
        int? port = null,
        Action<MqttClientTlsOptionsBuilder>? tls = null,
        bool authenticate = true,
        string password = MosquittoBroker.Password) =>
        Options(MosquittoBroker.ServerName, port ?? broker.Port, clientId, cleanSession, keepAlive, tls, authenticate, password);

    public static MqttClientOptions Options(
        string host,
        int port,
        string clientId,
        bool cleanSession = true,
        TimeSpan? keepAlive = null,
        Action<MqttClientTlsOptionsBuilder>? tls = null,
        bool authenticate = true,
        string password = MosquittoBroker.Password)
    {
        // Broker 只映射到 IPv4 回环地址；localhost 可能先解析为 ::1，所以限定 IPv4。
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port, AddressFamily.InterNetwork)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId(clientId)
            .WithCleanSession(cleanSession)
            .WithKeepAlivePeriod(keepAlive ?? TimeSpan.FromSeconds(30))
            .WithTimeout(OperationTimeout);
        if (authenticate)
        {
            builder.WithCredentials(MosquittoBroker.UserName, password);
        }

        if (tls is not null)
        {
            builder.WithTlsOptions(tls);
        }

        return builder.Build();
    }

    /// <summary>连接并要求 CONNACK 成功。</summary>
    public static async Task<(IMqttClient Client, MqttClientConnectResult Result)> ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            using var timeout = Timeout(cancellationToken);
            var result = await client.ConnectAsync(options, timeout.Token);
            Assert.Equal(MqttClientConnectResultCode.Success, result.ResultCode);
            return (client, result);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task SubscribeAsync(IMqttClient client, string topic, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        var result = await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            timeout.Token);
        Assert.Equal(MqttClientSubscribeResultCode.GrantedQoS1, Assert.Single(result.Items).ResultCode);
    }

    public static Task<MqttClientPublishResult> PublishAsync(
        IMqttClient client,
        string topic,
        byte[] payload,
        MqttQualityOfServiceLevel qualityOfService,
        CancellationToken cancellationToken) =>
        client.PublishAsync(
            new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithQualityOfServiceLevel(qualityOfService).Build(),
            cancellationToken);

    public static async Task DisconnectAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
    }

    public static CancellationTokenSource Timeout(CancellationToken cancellationToken, TimeSpan? after = null)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(after ?? OperationTimeout);
        return timeout;
    }

    public static string NewTopic(string scenario) => $"tenonedge/probe/{scenario}/{Guid.NewGuid():N}";

    public static async Task<(Exception? Exception, TimeSpan Elapsed)> MeasureAsync(Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(operation);
        return (exception, stopwatch.Elapsed);
    }

    public static string Describe(Exception? exception) =>
        exception is null ? "无异常" : $"{exception.GetType().FullName}：{exception.Message}"
            + (exception.InnerException is { } inner ? $"（内部 {inner.GetType().FullName}：{inner.Message}）" : string.Empty);
}

/// <summary>订阅者收到的消息，按到达顺序排队；可以关闭自动确认，由测试自己确认。</summary>
internal sealed class ReceivedMessages
{
    private readonly Channel<MqttApplicationMessageReceivedEventArgs> _messages = Channel.CreateUnbounded<MqttApplicationMessageReceivedEventArgs>();

    public static ReceivedMessages Attach(IMqttClient client, bool autoAcknowledge = true)
    {
        var received = new ReceivedMessages();
        client.ApplicationMessageReceivedAsync += args =>
        {
            // 必须在处理器返回之前决定：返回后库按 AutoAcknowledge 自动发送 PUBACK。
            args.AutoAcknowledge = autoAcknowledge;
            received._messages.Writer.TryWrite(args);
            return Task.CompletedTask;
        };
        return received;
    }

    public async Task<MqttApplicationMessageReceivedEventArgs> NextAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = MqttProbe.Timeout(cancellationToken, timeout);
        return await _messages.Reader.ReadAsync(deadline.Token);
    }

    /// <summary>等待一段时间，返回期间收到的全部消息。</summary>
    public async Task<List<MqttApplicationMessageReceivedEventArgs>> CollectAsync(TimeSpan period, CancellationToken cancellationToken)
    {
        await Task.Delay(period, cancellationToken);
        var collected = new List<MqttApplicationMessageReceivedEventArgs>();
        while (_messages.Reader.TryRead(out var message))
        {
            collected.Add(message);
        }

        return collected;
    }
}

using System.Net.Sockets;
using System.Security.Authentication;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Mqtt;

public sealed record MqttConnectionTestResult(bool Success, string Code, int? ConnAckCode, double ElapsedMs, ConnectionTestStep[] Steps, ServerCertificateInfo? Certificate = null);

/// <summary>
/// 显式触发的 MQTT 连接测试：独立随机 ClientId、CleanSession，不订阅、不发布、不写队列。
/// 复用正式出站连接器与连接级 TLS 信任校验；调用方负责权限、准入和凭证接收方绑定。
/// 方法返回前释放真实客户端，不能用取消等待代替释放。
/// </summary>
public sealed class MqttConnectionTest(IOutboundConnector connector, TimeProvider time, ITlsTrustResolver? trust = null)
{
    public async Task<MqttConnectionTestResult> RunAsync(MqttConnectionSettings settings, MqttCredential? credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.ValidateConnection();
        if (settings.UsePassword != (credential is not null))
        { throw new ArgumentException("测试认证方式与凭证不一致。"); }
        if (credential is not null && (string.IsNullOrEmpty(credential.Username) || credential.Username.Length > 256 ||
            credential.Password is null || credential.Password.Length > 4096 || credential.Username.Contains('\0', StringComparison.Ordinal) ||
            credential.Password.Contains('\0', StringComparison.Ordinal)))
        { throw new ArgumentException("测试凭证格式无效。"); }
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var trace = new ConnectionTestTrace(time, settings.Tls);
        var snapshot = settings.Tls ? trust?.ResolveTlsTrust(settings.ConnectionId, settings.Recipient) : null;
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, AdmitFrame,
            connected: () => trace.Begin("protocol"), offlineTrust: settings.Tls && snapshot is not null));
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithStreamProvider(new MqttStreamProvider(new ObservedTestConnector(connector, trace)))
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithClientId("te-test-" + Guid.NewGuid().ToString("N"))
            .WithKeepAlivePeriod(TimeSpan.Zero)
            .WithTimeout(TimeSpan.FromMilliseconds(settings.TimeoutMs));
        if (credential is not null)
        { options.WithCredentials(credential.Username, credential.Password); }
        string? certificateFailure = null;
        ServerCertificateInfo? serverCertificate = null;
        if (settings.Tls)
        {
            options.WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(args =>
            {
                Volatile.Write(ref serverCertificate, ServerCertificateTrust.Describe(args.Certificate));
                var failure = ServerCertificateTrust.Evaluate(args.Certificate, args.Chain, args.SslPolicyErrors, snapshot);
                Volatile.Write(ref certificateFailure, failure);
                return failure is null;
            }));
        }
        try
        {
            var result = await client.ConnectAsync(options.Build(), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return Result(result.ResultCode == MqttClientConnectResultCode.Success ? "success" : result.ResultCode switch
            {
                MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized => "credential.rejected",
                MqttClientConnectResultCode.ServerUnavailable => "broker.unavailable",
                _ => "protocol.rejected",
            }, (int)result.ResultCode);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or
            AuthenticationException or MqttCommunicationException or MqttProtocolViolationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result(deadline.IsCancellationRequested ? "timeout" : Volatile.Read(ref certificateFailure) ?? Classify(exception), null);
        }

        MqttConnectionTestResult Result(string code, int? connAckCode) =>
            new(code == "success", code, connAckCode, time.GetElapsedTime(started).TotalMilliseconds, trace.FinishMqtt(code), Volatile.Read(ref serverCertificate));
    }

    private static bool AdmitFrame(byte header, int remainingLength)
    {
        // 本测试只等 CONNACK；在库分配正文前拒绝任何 PUBLISH、错误长度或其他控制报文。
        if (header != 0x20 || remainingLength != 2)
        { throw new MqttProtocolViolationException("连接测试收到非预期控制报文。"); }
        return true;
    }

    private static string Classify(Exception exception)
    {
        if (exception is OutboundAccessDeniedException)
        { return "outbound.denied"; }
        if (exception is AuthenticationException)
        { return "certificate.invalid"; }
        if (exception is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "dns.failed",
                SocketError.ConnectionRefused => "connection.refused",
                SocketError.TimedOut => "timeout",
                _ => "connection.failed",
            };
        }
        if (exception is MqttCommunicationTimedOutException or OperationCanceledException)
        { return "timeout"; }
        if (exception.InnerException is { } inner)
        { return Classify(inner); }
        return exception is MqttProtocolViolationException ? "protocol.invalid" : "connection.failed";
    }
}

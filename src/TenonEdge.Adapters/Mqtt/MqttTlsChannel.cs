using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Channel;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>所有 MQTT TLS 使用的 BCL 通道。MQTTnet 5.2 的 TLS 选项不能禁用证书 AIA 下载，
/// 因此在原受控 TCP StreamProvider 之上显式提供离线链策略；MQTT 编解码、收发限制和确认仍由原适配器处理。
/// 默认使用系统根，连接信任由原回调判断。没有关闭验证或另建 TCP 连接，不声明在线吊销验证。</summary>
internal sealed class MqttTlsChannel(MqttClientTcpOptions options, TlsClientCertificate? clientCertificate = null) : IMqttChannel
{
    private SslStream? _stream;
    private int _disposed;
    public X509Certificate2 ClientCertificate => _stream?.LocalCertificate as X509Certificate2 ?? null!;
    public EndPoint LocalEndPoint => null!;
    public EndPoint RemoteEndPoint => options.RemoteEndpoint;
    public bool IsSecureConnection => true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (options.RemoteEndpoint is not DnsEndPoint endpoint || options.StreamProvider is null ||
            options.TlsOptions is not { UseTls: true, CertificateValidationHandler: not null } tls ||
            tls.ClientCertificatesProvider is not null || tls.CertificateSelectionHandler is not null)
        { throw new InvalidOperationException("受控 TLS 通道参数无效。"); }
        // 私钥由连接/测试的所有者持有；通道只借用，不选择系统证书库里的其他身份。
        var clientContext = clientCertificate?.Context(DateTimeOffset.UtcNow);
        var network = await options.StreamProvider.ConnectAsync(endpoint, cancellationToken);
        SslStream stream;
        try
        {
            stream = new SslStream(network, leaveInnerStreamOpen: false, (_, certificate, chain, errors) =>
                tls.CertificateValidationHandler(new(certificate, chain, errors, options)));
        }
        catch { network.Dispose(); throw; }
        if (Interlocked.CompareExchange(ref _stream, stream, null) is not null)
        { stream.Dispose(); throw new InvalidOperationException("TLS 通道不能重复连接。"); }
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Host,
                EnabledSslProtocols = tls.SslProtocol,
                CertificateChainPolicy = ServerCertificateTrust.OfflineChainPolicy(),
                ClientCertificateContext = clientContext,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                AllowRenegotiation = tls.AllowRenegotiation,
            }, cancellationToken);
        }
        catch { Dispose(); throw; }
    }

    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Stream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public async Task WriteAsync(ReadOnlySequence<byte> buffer, bool isEndOfPacket, CancellationToken cancellationToken)
    {
        var stream = Stream;
        foreach (var segment in buffer)
        { await stream.WriteAsync(segment, cancellationToken); }
    }

    private SslStream Stream => Volatile.Read(ref _stream) ?? throw new IOException("TLS 通道已关闭。");
    public Task DisconnectAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}

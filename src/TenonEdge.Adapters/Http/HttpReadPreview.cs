using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>一次真实 GET，与正式 HTTP 采集/输出串行；不写调试缓存或发送队列。</summary>
public sealed class HttpReadPreview(ISharedConnections connections, IOutboundConnector connector,
    IHttpCredentialResolver credentials, IJsonInputDecoderFactory decoders, TimeProvider time)
{
    public async Task<JsonReadPreviewResult> RunAsync(HttpInputSettings settings, ClockSyncState clockState, CancellationToken cancellationToken)
    {
        settings = (HttpInputSettings)settings.ValidateRequest();
        if (settings.PeriodMs is < 1000 or > 86400000 || settings.Fields.ValueKind != JsonValueKind.Array)
        { throw new ArgumentException("HTTP 来源周期或字段无效。"); }
        if (!Enum.IsDefined(clockState))
        { throw new ArgumentException("校时状态无效。"); }
        var decoder = settings.Fields.GetArrayLength() == 0 ? null : decoders.Build(settings.Fields, settings.UpstreamTime);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        IConnectionLease<HttpConnectionSession> lease;
        try
        { lease = connections.Acquire(settings.ConnectionId, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs)); }
        catch (InvalidOperationException) { throw new HttpConnectionUnavailableException(); }
        await using var ownership = lease;
        try
        {
            var result = await lease.ExecuteAsync((session, token) => session.ReadPreviewAsync(settings, credentials, decoder, clockState, time, token), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return JsonReadPreviewResult.Failure("timeout"); }
        catch (HttpCredentialUnavailableException)
        { return JsonReadPreviewResult.Failure("credential.unavailable"); }
        catch (InputMessageSizeException)
        { return JsonReadPreviewResult.Failure("fields.tooLarge"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or AuthenticationException)
        { return JsonReadPreviewResult.Failure(Classify(exception)); }
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
                _ => "connection.failed"
            };
        }
        return exception.InnerException is { } inner ? Classify(inner) : "connection.failed";
    }
}

internal sealed class HttpCredentialUnavailableException : InvalidOperationException
{
    public HttpCredentialUnavailableException() : base("HTTP 凭证不可用或正在变化。") { }
}

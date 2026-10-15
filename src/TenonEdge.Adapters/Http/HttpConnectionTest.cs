using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

public sealed record HttpConnectionTestResult(bool Success, string Code, int? StatusCode, double ElapsedMs, ConnectionTestStep[] Steps, ServerCertificateInfo? Certificate = null);

/// <summary>通过运行时共享连接的串行租约测试基础地址；重建空闲客户端后只做一次 GET，不跟随重定向或读取响应正文。</summary>
public sealed class HttpConnectionTest(ISharedConnections connections, IOutboundConnector connector, TimeProvider time, ITlsTrustResolver? trust = null)
{
    public async Task<HttpConnectionTestResult> RunAsync(HttpRequestSettings settings, HttpCredential? credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.ValidateRequest();
        if (settings.Path.Length != 0)
        { throw new ArgumentException("连接测试只允许基础地址。"); }
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUri)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        foreach (var header in settings.Headers)
        { request.Headers.Add(header.Key, header.Value); }
        if (credential is not null)
        { HttpConnectionSession.ApplyCredential(request, credential); }
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        IConnectionLease<HttpConnectionSession> lease;
        try
        { lease = connections.Acquire(settings.ConnectionId, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs, trust)); }
        catch (InvalidOperationException) { throw new HttpConnectionUnavailableException(); }
        await using var ownership = lease;
        try
        {
            var result = await lease.ExecuteAsync((session, token) => session.TestConnectionAsync(settings, request, credential is not null, time, token), linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return result with { ElapsedMs = time.GetElapsedTime(started).TotalMilliseconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var trace = new ConnectionTestTrace(time, settings.BaseUri.Scheme == "https");
            return new(false, "connection.busy", null, time.GetElapsedTime(started).TotalMilliseconds,
                trace.FinishHttp("connection.busy", null, credential is not null));
        }
    }

    internal static async Task<HttpConnectionTestResult> ExecuteAsync(HttpRequestSettings settings, HttpRequestMessage request,
        bool credentialUsed, IOutboundConnector connector, TimeProvider time, CancellationToken token, TlsTrustSnapshot? trust = null)
    {
        var started = time.GetTimestamp();
        var trace = new ConnectionTestTrace(time, settings.BaseUri.Scheme == "https");
        string? certificateFailure = null;
        ServerCertificateInfo? serverCertificate = null;
        using var handler = OutboundHttpTransport.CreateHandler(new ObservedTestConnector(connector, trace));
        handler.MaxResponseHeadersLength = 64;
        if (trust is not null)
        { handler.SslOptions.CertificateChainPolicy = ServerCertificateTrust.OfflineChainPolicy(); }
        handler.PlaintextStreamFilter = (context, _) =>
        {
            trace.Begin("protocol");
            return ValueTask.FromResult(context.PlaintextStream);
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            Volatile.Write(ref serverCertificate, ServerCertificateTrust.Describe(certificate));
            var failure = ServerCertificateTrust.Evaluate(certificate, chain, errors, trust);
            Volatile.Write(ref certificateFailure, failure);
            return failure is null;
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            token.ThrowIfCancellationRequested();
            var status = (int)response.StatusCode;
            var code = status is 401 or 403 ? "credential.rejected" : "http.response";
            return Result(code, status, response.IsSuccessStatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or AuthenticationException or OperationCanceledException)
        {
            return Result(Volatile.Read(ref certificateFailure) ?? Classify(exception), null, false);
        }

        HttpConnectionTestResult Result(string code, int? status, bool success) =>
            new(success, code, status, time.GetElapsedTime(started).TotalMilliseconds, trace.FinishHttp(code, status, credentialUsed), Volatile.Read(ref serverCertificate));
    }

    private static string Classify(Exception exception)
    {
        if (exception is OutboundAccessDeniedException)
        { return "outbound.denied"; }
        if (exception is AuthenticationException)
        { return "certificate.invalid"; }
        if (exception is OperationCanceledException)
        { return "timeout"; }
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
        if (exception.InnerException is { } inner)
        { return Classify(inner); }
        return exception is HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError }
            ? "protocol.invalid" : "connection.failed";
    }
}

public sealed class HttpConnectionUnavailableException : Exception
{
    public HttpConnectionUnavailableException() : base("共享 HTTP 会话不能借用。") { }
}

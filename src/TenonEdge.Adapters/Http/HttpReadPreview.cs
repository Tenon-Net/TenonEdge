using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>一次真实 GET，与正式 HTTP 采集/输出串行；不写调试缓存或发送队列。</summary>
public sealed class HttpReadPreview(ISharedConnections connections, IOutboundConnector connector,
    IHttpCredentialResolver credentials, IJsonInputDecoderFactory decoders, TimeProvider time, ITlsTrustResolver? trust = null)
{
    public Task<JsonReadPreviewResult> RunAsync(HttpInputSettings settings, ClockSyncState clockState, CancellationToken cancellationToken,
        int? timeoutMs = null, int maximumDisplayBytes = JsonReadPreview.MaximumDisplayBytes)
    {
        if (maximumDisplayBytes is < 2048 or > JsonReadPreview.MaximumDisplayBytes)
        { throw new ArgumentException("HTTP 预览展示预算无效。"); }
        return ReadAsync(settings, clockState, timeoutMs,
            (session, validated, decoder, token) => session.ReadPreviewAsync(validated, credentials, decoder, clockState, time, maximumDisplayBytes, token),
            code => JsonReadPreviewResult.Failure(code), cancellationToken);
    }

    public Task<JsonReadSampleResult> ReadSampleAsync(HttpInputSettings settings, string inputKey, string deviceKey,
        ClockSyncState clockState, CancellationToken cancellationToken)
    {
        BusinessKeys.Validate(inputKey, nameof(inputKey));
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        if (settings.Fields.ValueKind != JsonValueKind.Array || settings.Fields.GetArrayLength() == 0)
        { throw new ArgumentException("完整路线样本须有正式字段定义；空字段仅用于诊断展示/推断。"); }
        return ReadAsync(settings, clockState, null,
            (session, validated, decoder, token) => session.ReadSampleAsync(validated, credentials, decoder!, inputKey, deviceKey, clockState, time, token),
            JsonReadSampleResult.Failure, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpInputSettings settings, ClockSyncState clockState,
        int? timeoutMs, Func<HttpConnectionSession, HttpInputSettings, IJsonInputDecoder?, CancellationToken, Task<T>> read, Func<string, T> failure, CancellationToken cancellationToken)
    {
        settings = (HttpInputSettings)settings.ValidateRequest();
        if (settings.PeriodMs is < 1000 or > 86400000 || settings.Fields.ValueKind != JsonValueKind.Array)
        { throw new ArgumentException("HTTP 来源周期或字段无效。"); }
        if (!Enum.IsDefined(clockState))
        { throw new ArgumentException("校时状态无效。"); }
        if (timeoutMs is < 1 || timeoutMs > settings.TimeoutMs)
        { throw new ArgumentException("HTTP 预览剩余预算无效。"); }
        var decoder = settings.Fields.GetArrayLength() == 0 ? null : decoders.Build(settings.Fields, settings.UpstreamTime);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs ?? settings.TimeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        IConnectionLease<HttpConnectionSession> lease;
        try
        { lease = connections.Acquire(settings.ConnectionId, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs, trust, time)); }
        catch (InvalidOperationException) { throw new HttpConnectionUnavailableException(); }
        await using var ownership = lease;
        try
        {
            var result = await lease.ExecuteAsync((session, token) => read(session, settings, decoder, token), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return failure("timeout"); }
        catch (HttpCredentialUnavailableException)
        { return failure("credential.unavailable"); }
        catch (InputMessageSizeException)
        { return failure("fields.tooLarge"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or AuthenticationException)
        { return failure(Classify(exception)); }
    }

    public async Task<HttpDevicePreviewResult> RunSourcesAsync(IReadOnlyList<HttpPreviewSource> sources,
        ClockSyncState clockState, CancellationToken cancellationToken, int timeoutMs = 30000)
    {
        if (sources.Count == 0 || timeoutMs is < 100 or > 60000 || !Enum.IsDefined(clockState))
        { throw new ArgumentException("HTTP 预览来源或期限无效。"); }
        var started = time.GetTimestamp();
        var validated = new List<HttpPreviewSource>(sources.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            BusinessKeys.Validate(source.Key, nameof(sources));
            if (!keys.Add(source.Key))
            { throw new ArgumentException("HTTP 来源 key 重复。"); }
            var settings = (HttpInputSettings)source.Settings.ValidateRequest();
            if (settings.Fields.ValueKind != JsonValueKind.Array || settings.PeriodMs is < 1000 or > 86400000)
            { throw new ArgumentException("HTTP 来源字段或周期无效。"); }
            if (settings.Fields.GetArrayLength() != 0)
            { _ = decoders.Build(settings.Fields, settings.UpstreamTime); }
            validated.Add(new(source.Key, settings));
        }
        var basis = validated[0].Settings;
        if (validated.Any(source => source.Settings.ConnectionId != basis.ConnectionId || source.Settings.BaseAddress != basis.BaseAddress || source.Settings.TimeoutMs != basis.TimeoutMs))
        { throw new ArgumentException("同一设备须使用同一 HTTP 连接。"); }
        cancellationToken.ThrowIfCancellationRequested();
        var identity = JsonSerializer.Serialize(new { basis.BaseAddress, basis.TimeoutMs });
        IConnectionLease<HttpConnectionSession> lease;
        try
        { lease = connections.Acquire(basis.ConnectionId, "http", identity, () => new HttpConnectionSession(connector, basis.TimeoutMs, trust, time)); }
        catch (InvalidOperationException) { throw new HttpConnectionUnavailableException(); }
        await using var ownership = lease;
        var results = new List<HttpSourcePreviewResult>();
        var bytes = 1024;
        var truncated = false;
        var expired = false;
        foreach (var source in validated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeoutMs - time.GetElapsedTime(started).TotalMilliseconds;
            if (remaining < 1)
            { expired = true; break; }
            var capacity = JsonReadPreview.MaximumDisplayBytes - bytes - 1024;
            if (capacity < 4096)
            { truncated = true; break; }
            var result = await RunAsync(source.Settings, clockState, cancellationToken,
                Math.Min(source.Settings.TimeoutMs, (int)remaining), capacity);
            var entry = new HttpSourcePreviewResult(source.Key, result);
            bytes += JsonSerializer.SerializeToUtf8Bytes(entry).Length + 1;
            results.Add(entry);
            truncated |= result.RawTruncated || result.FieldsTruncated;
        }
        expired |= time.GetElapsedTime(started).TotalMilliseconds >= timeoutMs;
        return new(sources.Count, truncated, expired, time.GetElapsedTime(started).TotalMilliseconds, results.ToArray());
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

public sealed record HttpPreviewSource(string Key, HttpInputSettings Settings);
public sealed record HttpSourcePreviewResult(string Key, JsonReadPreviewResult Preview);
public sealed record HttpDevicePreviewResult(int TotalSources, bool Truncated, bool DeadlineExceeded, double ElapsedMs, HttpSourcePreviewResult[] Sources);

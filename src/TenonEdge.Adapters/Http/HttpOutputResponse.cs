using System.Buffers;
using System.Net;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Http;

/// <summary>固定 JSON 字段与固定标量等值判断，不执行表达式，不把业务响应正文写入错误信息。</summary>
public sealed class HttpSuccessRule
{
    private readonly JsonFieldPointer _location;
    private readonly JsonElement _expected;

    public HttpSuccessRule(string path, JsonElement expected)
    {
        if (expected.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array)
        { throw new ArgumentException("HTTP 成功判定只支持固定标量或 null。", nameof(expected)); }
        _location = new JsonFieldPointer(path);
        _expected = expected.Clone();
    }

    internal bool Matches(JsonElement body) => _location.TryResolve(body, out var value) && JsonElement.DeepEquals(value, _expected);
}

/// <summary>通用 HTTP 输出四类结果（设计 §8.2），不适用于 TenonIoT 的额外业务码分类。</summary>
public sealed class HttpOutputResponse(HttpSuccessRule? successRule = null)
{
    // 与发送测试报文的响应读取上限一致；只在配置了业务成功判定时读取正文。
    public const int MaximumBodyBytes = 64 * 1024;
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>调用方以 ResponseHeadersRead 获取响应，并以包含排队/连接/正文的总期限调用本方法，随后释放响应。</summary>
    public async Task<OutputSendResult> EvaluateAsync(HttpResponseMessage response, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        var status = (int)response.StatusCode;
        if (status is >= 200 and <= 299)
        {
            if (successRule is null)
            { return new(OutputSendStatus.Ack); }
            return await EvaluateBodyAsync(response, now, cancellationToken);
        }
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        { return new(OutputSendStatus.RejectMessage, $"HTTP {status} 拒绝本条报文。"); }
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        { return Retry(response, now, false); }
        if (status is >= 300 and <= 499)
        { return new(OutputSendStatus.SuspendTarget, $"HTTP {status} 需要检查目标配置或凭证。"); }
        return Retry(response, now, status is not (502 or 503 or 504));
    }

    private async Task<OutputSendResult> EvaluateBodyAsync(HttpResponseMessage response, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumBodyBytes)
        { return Retry(response, now, true); }
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumBodyBytes + 1);
        try
        {
            // 未知长度最多读上限加一字节用于判断超限，不调用无界 ReadAsStringAsync。
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var length = 0;
            while (length <= MaximumBodyBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, MaximumBodyBytes + 1 - length), cancellationToken);
                if (read == 0)
                { break; }
                length += read;
            }
            if (length > MaximumBodyBytes)
            { return Retry(response, now, true); }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            return successRule!.Matches(document.RootElement) ? new(OutputSendStatus.Ack) : Retry(response, now, true);
        }
        catch (JsonException) { return Retry(response, now, true); }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // 已有响应头不意味着响应正文已收到；中途断连仍是网络错误，不计毒消息。
            return Retry(response, now, false);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static OutputSendResult Retry(HttpResponseMessage response, DateTimeOffset now, bool countsTowardPoison)
    {
        var header = response.Headers.RetryAfter;
        var requested = header?.Delta ?? (header?.Date is { } date ? date - now : (TimeSpan?)null);
        var delay = requested > TimeSpan.Zero ? (requested > MaximumRetryAfter ? MaximumRetryAfter : requested) : null;
        return new(OutputSendStatus.Retry, "HTTP 临时失败或响应不满足成功约定，记录保持待发。", countsTowardPoison, delay);
    }
}

using System.Buffers;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Http;

/// <summary>测试响应只在内存展示，至多读取64 KiB正文，不用额外探测字节越过总设计上限。
/// 未知长度在上限处仍未观察到EOF时保守标记截断，即使它恰好等于上限。
/// 超限/不完整正文不能完整检查敏感字段和转义凭证，故明确标记截断并隐藏，不回显未经检查的前缀。</summary>
internal static class HttpTestResponseCapture
{
    internal const int MaximumBodyBytes = 64 * 1024;

    internal static async Task<HttpTestSendResult> ReadAsync(HttpResponseMessage response, HttpCredential? credential, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        if (response.Content.Headers.ContentLength > MaximumBodyBytes)
        { return Result(HttpResponseSummaryState.TooLarge); }
        if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
            response.Content.Headers.ContentType?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        { return Result(HttpResponseSummaryState.UnsupportedEncoding); }
        var media = response.Content.Headers.ContentType?.MediaType;
        var json = media is not null && (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
        if (media is not null && !json && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        { return Result(HttpResponseSummaryState.UnsupportedMediaType); }
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumBodyBytes);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var length = 0;
            while (length < MaximumBodyBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, MaximumBodyBytes - length), token);
                if (read == 0)
                { break; }
                length += read;
            }
            if (length == MaximumBodyBytes && response.Content.Headers.ContentLength != length)
            { return Result(HttpResponseSummaryState.TooLarge); }
            var state = HttpResponseBodyPrivacy.Inspect(buffer.AsSpan(0, length), credential, json, out var text);
            return Result(state, text);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or HttpRequestException)
        {
            // 已收到状态码；正文失败不能把已知HTTP响应变成“未发送”或触发重放。
            // 调用者仍须在此返回后传播显式用户取消。
            return Result(HttpResponseSummaryState.Unavailable);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

        HttpTestSendResult Result(HttpResponseSummaryState state, string? text = null) =>
            new("http.response", status, state, text, state == HttpResponseSummaryState.TooLarge, 0);
    }
}

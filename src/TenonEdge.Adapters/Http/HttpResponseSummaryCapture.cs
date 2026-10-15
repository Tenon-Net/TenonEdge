using System.Buffers;
using System.Text;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Http;

/// <summary>可选诊断，不决定投递分类。最多读64 KiB+1，完整检查后只保留1 KiB；超限不保留未经完整检查的前缀。
/// 获得错误状态后，正文的慢速、断连或取消只使摘要不可用，不能把已知拒绝/暂停重新变为可重试。</summary>
internal static class HttpResponseSummaryCapture
{
    internal const int MaximumBodyBytes = 64 * 1024;
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(250);

    internal static async Task<HttpResponseSummary> ReadAsync(HttpResponseMessage response, HttpCredential? credential, TimeProvider time, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        if (response.Content.Headers.ContentLength > MaximumBodyBytes)
        { return new(status, HttpResponseSummaryState.TooLarge); }
        if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
            response.Content.Headers.ContentType?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        { return new(status, HttpResponseSummaryState.UnsupportedEncoding); }
        var media = response.Content.Headers.ContentType?.MediaType;
        var json = media is not null && (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
        if (media is not null && !json && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        { return new(status, HttpResponseSummaryState.UnsupportedMediaType); }
        using var deadline = new CancellationTokenSource(CaptureTimeout, time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumBodyBytes + 1);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(operation.Token);
            var length = 0;
            while (length <= MaximumBodyBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, MaximumBodyBytes + 1 - length), operation.Token);
                if (read == 0)
                { break; }
                length += read;
            }
            return length > MaximumBodyBytes ? new(status, HttpResponseSummaryState.TooLarge)
                : FromBody(status, buffer.AsSpan(0, length), credential, json);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or HttpRequestException)
        { return new(status, HttpResponseSummaryState.Unavailable); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    internal static HttpResponseSummary FromBody(int status, ReadOnlySpan<byte> body, HttpCredential? credential, bool requireJson)
    {
        if (body.Length > MaximumBodyBytes)
        { return new(status, HttpResponseSummaryState.TooLarge); }
        var state = HttpResponseBodyPrivacy.Inspect(body, credential, requireJson, out var captured);
        if (state != HttpResponseSummaryState.Captured)
        { return new(status, state); }
        var text = captured!;
        var chars = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > HttpResponseSummary.MaximumTextBytes)
            { break; }
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        return new(status, HttpResponseSummaryState.Captured, text[..chars], chars != text.Length);
    }
}

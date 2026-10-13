using System.Buffers;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>可选诊断，不决定投递分类。最多读64 KiB+1，完整检查后只保留1 KiB；超限不保留未经完整检查的前缀。
/// 获得错误状态后，正文的慢速、断连或取消只使摘要不可用，不能把已知拒绝/暂停重新变为可重试。</summary>
internal static class HttpResponseSummaryCapture
{
    internal const int MaximumBodyBytes = 64 * 1024;
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly UTF8Encoding Utf8 = new(false, true);

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
        string text;
        try
        { text = Utf8.GetString(body).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return new(status, HttpResponseSummaryState.UnsupportedEncoding); }
        if (string.IsNullOrWhiteSpace(text))
        { return new(status, HttpResponseSummaryState.Empty); }
        // 看似JSON但损坏时不把包含转义秘密的片段作为纯文本回传；完整JSON检查在截断之前。
        var first = text.AsSpan().TrimStart()[0];
        if (requireJson || first is '{' or '[' or '"')
        {
            try
            {
                using var document = JsonDocument.Parse(text, new() { MaxDepth = 64 });
                if (JsonReadPreview.IsSensitive(document.RootElement, credential, CancellationToken.None))
                { return new(status, HttpResponseSummaryState.Redacted); }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { return new(status, HttpResponseSummaryState.InvalidBody); }
        }
        if (JsonReadPreview.IsSensitiveText(text, credential))
        { return new(status, HttpResponseSummaryState.Redacted); }
        // 不把控制序列或双向排版控制保存给网页；普通换行/制表保留。正文始终作为纯文本显示。
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t') ||
            character is '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u202C' or '\u2066' or '\u2067' or '\u2068' or '\u2069'))
        { return new(status, HttpResponseSummaryState.InvalidBody); }
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

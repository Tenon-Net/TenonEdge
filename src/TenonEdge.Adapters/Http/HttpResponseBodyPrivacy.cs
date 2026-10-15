using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>正式失败摘要与测试响应共用的完整正文隐私检查；不检查不完整前缀，也不声称识别任意变换的秘密。</summary>
internal static class HttpResponseBodyPrivacy
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static HttpResponseSummaryState Inspect(ReadOnlySpan<byte> body, HttpCredential? credential, bool requireJson, out string? text)
    {
        text = null;
        string decoded;
        try
        { decoded = Utf8.GetString(body).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return HttpResponseSummaryState.UnsupportedEncoding; }
        if (string.IsNullOrWhiteSpace(decoded))
        { return HttpResponseSummaryState.Empty; }
        // 看似JSON但损坏时不把包含转义秘密的片段作为纯文本回传；完整JSON检查在截断之前。
        var first = decoded.AsSpan().TrimStart()[0];
        if (requireJson || first is '{' or '[' or '"')
        {
            try
            {
                using var document = JsonDocument.Parse(decoded, new() { MaxDepth = 64 });
                if (JsonReadPreview.IsSensitive(document.RootElement, credential, CancellationToken.None))
                { return HttpResponseSummaryState.Redacted; }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { return HttpResponseSummaryState.InvalidBody; }
        }
        if (JsonReadPreview.IsSensitiveText(decoded, credential))
        { return HttpResponseSummaryState.Redacted; }
        // 不把控制序列或双向排版控制保存给网页；普通换行/制表保留。正文始终作为纯文本显示。
        if (decoded.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t') ||
            character is '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u202C' or '\u2066' or '\u2067' or '\u2068' or '\u2069'))
        { return HttpResponseSummaryState.InvalidBody; }
        text = decoded;
        return HttpResponseSummaryState.Captured;
    }
}

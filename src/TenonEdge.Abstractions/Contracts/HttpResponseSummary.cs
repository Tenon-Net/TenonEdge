using System.Text;

namespace TenonEdge.Abstractions.Contracts;

public enum HttpResponseSummaryState
{
    Captured, Empty, TooLarge, UnsupportedEncoding, UnsupportedMediaType, InvalidBody, Redacted, Unavailable
}

/// <summary>有界的对方响应诊断正文，不是普通错误文本；只可按正文权限提供，不进入日志/运行事件或只读元数据。
/// 采集方须先检查完整的有界响应和实际本次凭证，再截断展示。构造函数只保证结构/大小，不能证明来源无秘密。</summary>
public sealed class HttpResponseSummary
{
    public const int MaximumTextBytes = 1024;
    public const int MaximumSerializedBytes = 8192;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public HttpResponseSummary(int statusCode, HttpResponseSummaryState state, string? text = null, bool truncated = false)
    {
        if (statusCode is < 100 or > 999 || !Enum.IsDefined(state))
        { throw new ArgumentException("响应摘要状态无效。"); }
        if (state == HttpResponseSummaryState.Captured ? string.IsNullOrEmpty(text) : text is not null || truncated)
        { throw new ArgumentException("响应摘要内容与状态不一致。"); }
        if (text is not null && Utf8.GetByteCount(text) > MaximumTextBytes)
        { throw new ArgumentException("响应摘要超过字节上限。"); }
        StatusCode = statusCode;
        State = state;
        Text = text;
        Truncated = truncated;
    }

    public int StatusCode { get; }
    public HttpResponseSummaryState State { get; }
    public string? Text { get; }
    public bool Truncated { get; }
    public override string ToString() => $"HTTP response summary ({StatusCode}, {State}; body omitted)";
}

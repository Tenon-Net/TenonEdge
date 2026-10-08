using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 已通过输入大小/深度校验的原始 JSON 的独立不可变副本。Clone 不借用协议读取器的池或可释放文档；
/// 多分支只共享只读根值，不各自复制。仅供显式 JSON Pointer 映射/预览，不自动序列化或回显正文。
/// </summary>
public sealed class RawJsonPayload
{
    public const int MaximumBytes = 1024 * 1024;
    // 保守覆盖 UTF-8 与 JsonDocument 节点元数据；最密集嵌套容器也为每字节不足 16 字节。
    public const long MaximumBudgetBytes = 16L * MaximumBytes + 1024;
    private readonly JsonElement _root;

    private RawJsonPayload(JsonElement root, int bytes)
    {
        _root = root.Clone();
        ByteLength = bytes;
    }

    public int ByteLength { get; }
    public long BudgetBytes => 16L * ByteLength + 1024;
    [JsonIgnore]
    public JsonElement Root => _root;

    /// <summary>调用方先限制输入文档（1 MiB、深度 64）；本方法再核对 UTF-8 大小后才复制树。</summary>
    public static RawJsonPayload CopyFrom(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Undefined)
        { throw new ArgumentException("原始 JSON 不能为空。"); }
        var text = root.GetRawText();
        if (text.Length > MaximumBytes)
        { throw new ArgumentException("原始 JSON 超过 1 MiB。"); }
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaximumBytes)
        { throw new ArgumentException("原始 JSON 超过 1 MiB。"); }
        return new(root, bytes);
    }

    public override string ToString() => $"JSON payload ({ByteLength} bytes; content omitted)";
}

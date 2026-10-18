using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>一个输入参数引用的非秘密连接快照。协议参数由宿主在冻结时校验；
/// 不包含凭证、活跃连接或数据库访问器。联网仍须借用原共享连接与凭证解析器。</summary>
public sealed class InputConnectionSnapshot
{
    public InputConnectionSnapshot(long connectionId, string kind, JsonElement configuration)
    {
        if (connectionId is < 1 or > 9007199254740991L || !BusinessKeys.IsValid(kind) || configuration.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("输入连接快照无效。"); }
        ConnectionId = connectionId;
        Kind = kind;
        using var document = JsonDocument.Parse(CanonicalJson.Serialize(configuration));
        Configuration = document.RootElement.Clone();
    }

    public long ConnectionId { get; }
    public string Kind { get; }
    public JsonElement Configuration { get; }
}

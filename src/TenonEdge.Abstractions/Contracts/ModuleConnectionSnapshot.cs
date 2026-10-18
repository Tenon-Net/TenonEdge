using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>一个模块参数引用的非秘密连接快照。协议参数由宿主在冻结时校验；
/// 不包含凭证、活跃连接或数据库访问器。联网仍须借用原共享连接与凭证解析器。</summary>
public sealed class ModuleConnectionSnapshot
{
    public ModuleConnectionSnapshot(long connectionId, string kind, JsonElement configuration)
    {
        if (connectionId is < 1 or > 9007199254740991L || !BusinessKeys.IsValid(kind) || configuration.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("模块连接快照无效。"); }
        ConnectionId = connectionId;
        Kind = kind;
        using var document = JsonDocument.Parse(CanonicalJson.Serialize(configuration));
        Configuration = document.RootElement.Clone();
    }

    public long ConnectionId { get; }
    public string Kind { get; }
    public JsonElement Configuration { get; }
    /// <summary>只读取原冻结声明版本与非秘密标量；不调用注册目录或适配器。
    /// 这不是新配置准入，冻结/构建仍须用当前目录 Validate。</summary>
    public static (int Version, JsonElement Parameters) ReadModuleConfiguration(ModuleConnectionSnapshot connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Configuration;
        if (!ConnectionModuleDefinition.IsModuleKind(connection.Kind) || config.ValueKind != JsonValueKind.Object ||
            config.EnumerateObject().Count() != 1 || !config.TryGetProperty("connectionModule", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
            envelope.EnumerateObject().Count() != 2 || !envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number < 1 || !envelope.TryGetProperty("parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(parameters.GetRawText()) > ParameterSchema.MaximumConfigurationBytes)
        { throw InvalidModuleConfiguration(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
        {
            if (names.Count >= ParameterSchema.MaximumParameters || !names.Add(property.Name) ||
                !TenonEdge.Abstractions.Messages.BusinessKeys.IsValid(property.Name) || property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            { throw InvalidModuleConfiguration(); }
        }
        return (number, parameters);
    }

    /// <summary>共享会话身份包含类型、版本和全部冻结参数，避免参数改变后误借旧客户端；不是输出接收方指纹。</summary>
    public static string ModuleConfigurationIdentity(ModuleConnectionSnapshot connection)
    {
        _ = ReadModuleConfiguration(connection);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(
            JsonSerializer.SerializeToElement(new { connection.Kind, connection.Configuration }))));
    }

    private static ArgumentException InvalidModuleConfiguration() => new("连接模块冻结参数无效。");
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Http;

/// <summary>每条 HTTP 投递自己的路径与成功判定；与当前输出定义及第一条路径的设置无关。</summary>
internal sealed record HttpFrozenRequest(int Version, string Path, string? SuccessPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement SuccessValue)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static string Freeze(HttpOutputSettings settings)
    {
        var json = JsonSerializer.Serialize(new HttpFrozenRequest(1, settings.Path, settings.SuccessPath, settings.SuccessValue), Options);
        CheckSize(json);
        return json;
    }

    public static HttpSuccessRule? Read(string metadata, string address)
    {
        CheckSize(metadata);
        var rule = JsonSerializer.Deserialize<HttpFrozenRequest>(metadata, Options) ?? throw new ArgumentException("冻结 HTTP 约定为空。");
        if (rule.Version != 1 || rule.Path != address || rule.Path is null ||
            (rule.SuccessPath is null) != (rule.SuccessValue.ValueKind == JsonValueKind.Undefined))
        { throw new ArgumentException("冻结 HTTP 约定无效。"); }
        return rule.SuccessPath is null ? null : new HttpSuccessRule(rule.SuccessPath, rule.SuccessValue);
    }

    private static void CheckSize(string metadata)
    {
        if (Encoding.UTF8.GetByteCount(metadata) > OutputMessage.MaximumProtocolMetadataBytes)
        { throw new ArgumentException("冻结 HTTP 约定超过 16 KiB。"); }
    }
}

using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Http;

public sealed record HttpOutputSettings : HttpRequestSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public ImmutableArray<string> Inputs { get; init; } = [];
    /// <summary>仅接续该连接已有的冻结队列，不是路线，也不从任何输入生成投递。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool QueueOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Mapping { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement MappingStages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Payload { get; init; }
    public string? SuccessPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement SuccessValue { get; init; }

    public static HttpOutputSettings Parse(JsonElement json)
    {
        var settings = json.Deserialize<HttpOutputSettings>(Options) ?? throw new ArgumentException("HTTP 配置不能为空。");
        settings = (HttpOutputSettings)settings.ValidateRequest();
        if (settings.QueueOnly)
        {
            if (settings.Inputs.IsDefault || settings.Inputs.Length != 0 || settings.Path != string.Empty || settings.Headers.Count != 0 ||
                settings.SuccessPath is not null || settings.SuccessValue.ValueKind != JsonValueKind.Undefined ||
                settings.Mapping.ValueKind != JsonValueKind.Undefined || settings.MappingStages.ValueKind != JsonValueKind.Undefined ||
                settings.Payload.ValueKind != JsonValueKind.Undefined)
            { throw new ArgumentException("仅发送冻结队列的 HTTP 目标不能声明输入、路径、头、成功规则、映射或报文格式。"); }
            return settings;
        }
        if (settings.Inputs.IsDefaultOrEmpty || settings.Inputs.Length > 2000 || settings.Inputs.Distinct(StringComparer.Ordinal).Count() != settings.Inputs.Length)
        { throw new ArgumentException("HTTP 输出须引用 1—2000 个不重复输入。"); }
        foreach (var input in settings.Inputs)
        {
            if (string.IsNullOrEmpty(input) || RuntimeUnitId.Create("input.simulator", input).KeySegments.Count != 2)
            { throw new ArgumentException("输入引用应为 deviceKey/inputKey。"); }
        }
        if ((settings.SuccessPath is null) != (settings.SuccessValue.ValueKind == JsonValueKind.Undefined))
        { throw new ArgumentException("HTTP 业务成功位置和值须同时配置。"); }
        if (settings.SuccessPath is { } path)
        { _ = new HttpSuccessRule(path, settings.SuccessValue); }
        _ = OutputPayloadFormat.Parse(settings.Payload);
        _ = HttpFrozenRequest.Freeze(settings);
        return settings with { SuccessValue = settings.SuccessValue.ValueKind == JsonValueKind.Undefined ? default : settings.SuccessValue.Clone() };
    }

    // §8.3：身份属于连接；输出路径由冻结报文与配置变更保护单独检查。凭证版本/头名称不代表接收账户。
    internal string Fingerprint(HttpCredentialIdentity? identity) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { BaseAddress, identity?.Kind.ToString() ?? "none",
            identity?.Username ?? "", identity?.AccountMarker ?? "" }))));

    /// <summary>将已经生成的完整正文冻结为一次请求，不重新格式化正文或生成编号；不连接、不读取秘密。</summary>
    public OutputMessage FreezePayload(ReadOnlyMemory<byte> payload, Guid deliveryId, HttpCredentialIdentity? identity)
    {
        if (QueueOnly || payload.IsEmpty || payload.Length > StandardEnvelope.MaximumPayloadBytes || deliveryId == Guid.Empty)
        { throw new ArgumentException("须提供有效完整报文及原投递编号。"); }
        if (identity is { Kind: HttpCredentialKind.ApiKeyHeader, Name: { } name } && Headers.ContainsKey(name))
        { throw new ArgumentException("API Key 请求头不能同时保存在非秘密静态请求头中。"); }
        return new(payload, Path, null, Fingerprint(identity), OutputRecipientIdentity.Http(BaseAddress, identity).Freeze(),
            Headers.SetItem("Idempotency-Key", deliveryId.ToString("D")), HttpFrozenRequest.Freeze(this));
    }
}

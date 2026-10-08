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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Mapping { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Payload { get; init; }
    public string? SuccessPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement SuccessValue { get; init; }

    public static HttpOutputSettings Parse(JsonElement json)
    {
        var settings = json.Deserialize<HttpOutputSettings>(Options) ?? throw new ArgumentException("HTTP 配置不能为空。");
        settings = (HttpOutputSettings)settings.ValidateRequest();
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
}

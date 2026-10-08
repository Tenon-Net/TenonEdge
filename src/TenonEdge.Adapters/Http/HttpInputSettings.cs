using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenonEdge.Adapters.Http;

public sealed record HttpInputSettings : HttpRequestSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public int PeriodMs { get; init; } = 5000;
    public JsonElement Fields { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement UpstreamTime { get; init; }

    public static HttpInputSettings Parse(JsonElement configuration)
    {
        var settings = configuration.Deserialize<HttpInputSettings>(Options) ?? throw new ArgumentException("HTTP 输入配置不能为空。");
        if (settings.PeriodMs is < 1000 or > 86400000)
        { throw new ArgumentException("HTTP 拉取周期须为 1 秒至 1 天。"); }
        if (settings.Fields.ValueKind != JsonValueKind.Array)
        { throw new ArgumentException("HTTP 输入须定义字段。"); }
        settings = (HttpInputSettings)settings.ValidateRequest();
        return settings with { Fields = settings.Fields.Clone(), UpstreamTime = settings.UpstreamTime.ValueKind == JsonValueKind.Undefined ? default : settings.UpstreamTime.Clone() };
    }
}

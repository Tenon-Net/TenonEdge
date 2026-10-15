using System.Net.Http.Headers;
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

    // 正式发送与一次测试共用的线级编码，不重新生成正文、幂等编号或请求头。
    // 调用方先检查冻结约定、身份和幂等编号；秘密仍由 ApplyCredential 单独加入。
    internal static HttpRequestMessage CreatePost(Uri endpoint, OutputMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            request.Content = new ReadOnlyMemoryContent(message.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            foreach (var pair in message.Headers!)
            {
                if (!pair.Key.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                { HttpOutputSettings.ValidateHeader(pair.Key, pair.Value); }
                request.Headers.Add(pair.Key, pair.Value);
            }
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private static void CheckSize(string metadata)
    {
        if (Encoding.UTF8.GetByteCount(metadata) > OutputMessage.MaximumProtocolMetadataBytes)
        { throw new ArgumentException("冻结 HTTP 约定超过 16 KiB。"); }
    }
}

using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Http;

public sealed record HttpOutputSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public long ConnectionId { get; init; }
    public string BaseAddress { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public int TimeoutMs { get; init; } = 5000;
    public ImmutableDictionary<string, string> Headers { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<string> Inputs { get; init; } = [];
    public string? SuccessPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement SuccessValue { get; init; }

    public Uri BaseUri => new(BaseAddress, UriKind.Absolute);
    public Uri Endpoint => ResolvePath(BaseUri, Path);

    public static HttpOutputSettings Parse(JsonElement json)
    {
        var settings = json.Deserialize<HttpOutputSettings>(Options) ?? throw new ArgumentException("HTTP 配置不能为空。");
        if (settings.ConnectionId is < 1 or > 9007199254740991L)
        { throw new ArgumentException("HTTP 连接编号无效。"); }
        if (settings.TimeoutMs is < 100 or > 60000)
        { throw new ArgumentException("HTTP 总超时须为 100—60000 毫秒。"); }
        if (!Uri.TryCreate(settings.BaseAddress, UriKind.Absolute, out var basis) || basis.Scheme is not ("http" or "https") ||
            basis.Port < 1 || basis.UserInfo.Length != 0 || basis.Query.Length != 0 || basis.Fragment.Length != 0 || string.IsNullOrEmpty(basis.Host))
        { throw new ArgumentException("HTTP 基础地址只允许不含用户信息、查询和片段的 HTTP(S) 地址。"); }
        settings = settings with { BaseAddress = basis.AbsoluteUri.TrimEnd('/') + "/" };
        _ = settings.Endpoint;
        if (settings.Headers is null || settings.Headers.Count > 64)
        { throw new ArgumentException("HTTP 静态请求头无效或超过 64 个。"); }
        var headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Headers)
        {
            ValidateHeader(pair.Key, pair.Value);
            if (!headers.TryAdd(pair.Key, pair.Value))
            { throw new ArgumentException("HTTP 请求头名称不能忽略大小写后重复。"); }
        }
        if (headers.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value)) > 16384)
        { throw new ArgumentException("HTTP 静态请求头合计超过 16 KiB。"); }
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
        return settings with { Headers = headers.ToImmutable(), SuccessValue = settings.SuccessValue.ValueKind == JsonValueKind.Undefined ? default : settings.SuccessValue.Clone() };
    }

    internal static Uri ResolvePath(Uri basis, string path)
    {
        if (path is null || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal) ||
            path.IndexOfAny(['#', '{', '}', '\r', '\n', '\0']) >= 0 || (!path.StartsWith('/') && Uri.TryCreate(path, UriKind.Absolute, out _)))
        { throw new ArgumentException("HTTP 输出路径必须是固定相对路径，不能切换主机或含片段/占位符。"); }
        var target = new Uri(basis, path.TrimStart('/'));
        var decoded = Uri.UnescapeDataString(target.AbsolutePath).Replace('\\', '/');
        var basePath = Uri.UnescapeDataString(basis.AbsolutePath);
        if (target.GetLeftPart(UriPartial.Authority) != basis.GetLeftPart(UriPartial.Authority) ||
            !decoded.StartsWith(basePath, StringComparison.Ordinal) || decoded.Contains('%', StringComparison.Ordinal) ||
            decoded.Split('/').Any(segment => segment is "." or ".."))
        { throw new ArgumentException("HTTP 输出路径不能越过基础路径或使用歧义转义。"); }
        return target;
    }

    internal static void ValidateHeader(string name, string value, bool credential = false)
    {
        if (string.IsNullOrEmpty(name) || value is null || value.Length > 4096 || value.Any(character => (character < ' ' && character != '\t') || character > '~') ||
            name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || (!credential && name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)))
        { throw new ArgumentException("HTTP 请求头无效、含认证内容或属于受控请求头。"); }
        using var request = new HttpRequestMessage();
        request.Headers.Add(name, value);
    }

    internal string Fingerprint(HttpCredentialIdentity? identity) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Endpoint.AbsoluteUri, identity?.Kind.ToString() ?? "none",
            identity?.Username ?? "", identity?.Name ?? "", identity?.AccountMarker ?? "" }))));
}

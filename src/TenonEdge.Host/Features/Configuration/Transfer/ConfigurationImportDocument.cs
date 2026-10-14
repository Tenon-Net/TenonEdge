using System.Text.Json;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal sealed record ConfigurationImportIssue(string Code, string Path);
internal sealed class ConfigurationImportException(ConfigurationImportIssue[] issues, bool truncated = false) : Exception("配置导入预检失败。")
{
    public ConfigurationImportIssue[] Issues { get; } = issues;
    public bool Truncated { get; } = truncated;
}

/// <summary>只解析上传字节；拒绝重复JSON属性、未知格式/成员及超限，不访问数据库、凭证或网络。</summary>
internal static class ConfigurationImportDocument
{
    private static readonly JsonSerializerOptions Json = new(ConfigurationExport.Json)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
    };

    public static PortableConfiguration Read(ReadOnlyMemory<byte> utf8, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (utf8.Length > ConfigurationExport.MaximumBytes)
        { throw Error("import.fileTooLarge", "$"); }
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            { throw Error("import.schema", "$"); }
            var issues = new ImportIssues();
            CheckProperties(root, "$", issues, token);
            issues.ThrowIfAny();
            if (!root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            { throw Error("import.formatVersion", "$.formatVersion"); }
            var result = root.Deserialize<PortableConfiguration>(Json) ?? throw Error("import.schema", "$");
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (JsonException error)
        { throw Error("import.schema", BoundedPath(error.Path ?? "$")); }
        catch (InvalidOperationException)
        { throw Error("import.schema", "$.formatVersion"); }
    }

    private static void CheckProperties(JsonElement value, string path, ImportIssues issues, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var child = BoundedPath(path + "[" + JsonSerializer.Serialize(property.Name) + "]");
                if (!names.Add(property.Name))
                { issues.Add("import.duplicateProperty", child); }
                CheckProperties(property.Value, child, issues, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            { CheckProperties(item, BoundedPath($"{path}[{index++}]"), issues, token); }
        }
    }

    internal static ConfigurationImportException Error(string code, string path) => new([new(code, path)]);
    private static string BoundedPath(string value) => value.Length <= 512 ? value : value[..509] + "...";
}

/// <summary>有界错误清单；达到上限时明确标记截断，不把剩余对象当作校验通过。</summary>
internal sealed class ImportIssues
{
    private readonly List<ConfigurationImportIssue> _issues = [];
    private bool _truncated;
    public void Add(string code, string path)
    {
        if (_issues.Count < 128)
        { _issues.Add(new(code, path)); }
        else
        { _truncated = true; }
    }
    public void ThrowIfAny()
    {
        if (_issues.Count != 0)
        { throw new ConfigurationImportException(_issues.ToArray(), _truncated); }
    }
}

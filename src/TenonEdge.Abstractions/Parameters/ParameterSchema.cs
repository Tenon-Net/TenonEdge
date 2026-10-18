using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace TenonEdge.Abstractions.Parameters;

/// <summary>不可变、无 I/O 的声明与校验。只处理六类标量；复杂点表/报文仍由适配器自身校验。
/// 引用按调用方在当前事务中读取的连接快照核对；凭证状态来自受保护存储，不接受客户端自报。
/// 不自动写默认值、修正类型、裁剪文字或转换数字；原始配置仍由原调用方保存。</summary>
public sealed class ParameterSchema
{
    public const int MaximumParameters = 128;
    public const int MaximumConfigurationBytes = 128 * 1024;
    private readonly ImmutableDictionary<string, ParameterDefinition> _byName;

    public string Id { get; }
    public ParameterScope Scope { get; }
    public ImmutableArray<ParameterDefinition> Parameters { get; }

    public ParameterSchema(string id, ParameterScope scope, IEnumerable<ParameterDefinition> parameters)
    {
        if (!Key(id) || !Enum.IsDefined(scope))
        { throw new ArgumentException("参数声明标识或用途无效。"); }
        ArgumentNullException.ThrowIfNull(parameters);
        var fields = parameters.Take(MaximumParameters + 1).ToImmutableArray();
        if (fields.Length is 0 or > MaximumParameters)
        { throw new ArgumentException("参数声明数量无效。"); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null || !Key(field.Name) || !names.Add(field.Name) || !Label(field.DisplayName, 128) ||
                !Enum.IsDefined(field.Type) || field.Description?.Length > 1024 || field.Group is { } group && !Label(group, 128) ||
                field.Options.IsDefault || field.ConnectionKinds.IsDefault)
            { throw new ArgumentException("参数声明字段无效或重复。"); }
            if (field.Type != ParameterType.Number && (field.WholeNumber || field.MinimumJson is not null || field.MaximumJson is not null) ||
                field.Type != ParameterType.Text && field.MaximumLength is not null || field.MaximumLength is < 1 or > 65536 ||
                field.Type == ParameterType.Text && field.MaximumLength is null ||
                field.Type == ParameterType.Credential && (scope != ParameterScope.Connection || field.DefaultJson is not null) ||
                field.Type == ParameterType.ConnectionReference && (field.DefaultJson is not null || field.ConnectionKinds.Length == 0) ||
                field.Type != ParameterType.ConnectionReference && field.ConnectionKinds.Length != 0 ||
                field.Type != ParameterType.Choice && field.Options.Length != 0 ||
                field.Type == ParameterType.Choice && field.Options.Length is 0 or > 128)
            { throw new ArgumentException("参数类型与约束不匹配。"); }
            if (field.Options.Any(option => option is null || !Label(option.Value, 128) || !Label(option.DisplayName, 128)) ||
                field.Options.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count() != field.Options.Length ||
                field.ConnectionKinds.Length > 128 || field.ConnectionKinds.Any(kind => !Key(kind)) ||
                field.ConnectionKinds.Distinct(StringComparer.Ordinal).Count() != field.ConnectionKinds.Length)
            { throw new ArgumentException("参数选项或连接类型无效。"); }
            try
            {
                var minimum = field.MinimumJson is { } min ? ParameterNumber.Read(min) : (ParameterNumber?)null;
                var maximum = field.MaximumJson is { } max ? ParameterNumber.Read(max) : (ParameterNumber?)null;
                if (minimum is { } lower && maximum is { } upper && lower.CompareTo(upper) > 0 ||
                    field.WholeNumber && (minimum is { WholeNumber: false } || maximum is { WholeNumber: false }))
                { throw new ArgumentException("参数数值范围无效。"); }
                if (field.DefaultJson is { } raw)
                {
                    if (Encoding.UTF8.GetByteCount(raw) > MaximumConfigurationBytes)
                    { throw new ArgumentException("默认值过大。"); }
                    using var value = JsonDocument.Parse(raw);
                    if (ValueIssue(field, value.RootElement, null) is not null)
                    { throw new ArgumentException("参数默认值不满足声明。"); }
                }
            }
            catch (JsonException) { throw new ArgumentException("参数默认值或边界不是有效 JSON。"); }
        }
        Id = id;
        Scope = scope;
        Parameters = fields;
        _byName = fields.ToImmutableDictionary(field => field.Name, StringComparer.Ordinal);
    }

    public ImmutableArray<ParameterIssue> Validate(JsonElement values, IReadOnlyDictionary<string, string>? connections = null,
        IReadOnlySet<string>? configuredCredentials = null)
    {
        if (values.ValueKind != JsonValueKind.Object)
        { return [new("", "parameters.object")]; }
        if (Encoding.UTF8.GetByteCount(values.GetRawText()) > MaximumConfigurationBytes)
        { return [new("", "parameters.size")]; }
        var issues = ImmutableArray.CreateBuilder<ParameterIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in values.EnumerateObject())
        {
            if (++count > MaximumParameters * 2)
            { return [new("", "parameters.size")]; }
            if (!_byName.TryGetValue(property.Name, out var field))
            { issues.Add(new("", "parameter.unknown")); continue; }
            if (!seen.Add(property.Name))
            { issues.Add(new(field.Name, "parameter.duplicate")); continue; }
            var error = ValueIssue(field, property.Value, connections);
            if (error is not null)
            { issues.Add(new(field.Name, error)); }
        }
        foreach (var field in Parameters)
        {
            if (field.Type == ParameterType.Credential)
            {
                if (field.Required && configuredCredentials?.Contains(field.Name) != true)
                { issues.Add(new(field.Name, "parameter.required")); }
            }
            else if (field.Required && !seen.Contains(field.Name))
            { issues.Add(new(field.Name, "parameter.required")); }
        }
        return issues.ToImmutable();
    }

    public void ThrowIfInvalid(JsonElement values, IReadOnlyDictionary<string, string>? connections = null,
        IReadOnlySet<string>? configuredCredentials = null)
    {
        if (!Validate(values, connections, configuredCredentials).IsEmpty)
        { throw new ArgumentException("参数不满足适配器声明。"); }
    }

    private static string? ValueIssue(ParameterDefinition field, JsonElement value, IReadOnlyDictionary<string, string>? connections)
    {
        // Even null/placeholder values must not be persisted in a public credential field.
        if (field.Type == ParameterType.Credential)
        { return "parameter.credentialSeparate"; }
        if (value.ValueKind == JsonValueKind.Null)
        { return field.Required ? "parameter.required" : null; }
        switch (field.Type)
        {
            case ParameterType.Text:
                if (value.ValueKind != JsonValueKind.String)
                { return "parameter.type"; }
                var text = value.GetString()!;
                return text.Length > field.MaximumLength ? "parameter.length" : field.Required && string.IsNullOrWhiteSpace(text) ? "parameter.required" : null;
            case ParameterType.Number:
                if (!ParameterNumber.TryRead(value, out var number))
                { return "parameter.number"; }
                if (field.WholeNumber && !number.WholeNumber)
                { return "parameter.integer"; }
                return field.MinimumJson is { } min && number.CompareTo(ParameterNumber.Read(min)) < 0 ||
                    field.MaximumJson is { } max && number.CompareTo(ParameterNumber.Read(max)) > 0 ? "parameter.range" : null;
            case ParameterType.Switch:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "parameter.type";
            case ParameterType.Choice:
                return value.ValueKind == JsonValueKind.String && field.Options.Any(option => option.Value == value.GetString()) ? null : "parameter.choice";
            case ParameterType.ConnectionReference:
                if (value.ValueKind != JsonValueKind.String || !long.TryParse(value.GetString(), out var id) || id is < 1 or > 9007199254740991L ||
                    value.GetString() != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                { return "parameter.connection"; }
                return connections?.TryGetValue(value.GetString()!, out var kind) == true && field.ConnectionKinds.Contains(kind, StringComparer.Ordinal)
                    ? null : "parameter.connection";
            default:
                return "parameter.type";
        }
    }

    private static bool Key(string? value) => TenonEdge.Abstractions.Messages.BusinessKeys.IsValid(value);
    private static bool Label(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

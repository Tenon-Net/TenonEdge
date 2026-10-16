using System.Collections.Immutable;

namespace TenonEdge.Abstractions.Parameters;

public enum ParameterType { Text, Number, Switch, Choice, Credential, ConnectionReference }
public enum ParameterScope { Connection, Input, Transform, Output }

/// <summary>编译时声明的标量参数。默认值及数值边界是 JSON 原文，不经 JavaScript double 往返。
/// 凭证只有声明和“已设置”状态；秘密必须使用 Host 的独立受保护写入流程，不属于配置 JSON。</summary>
public sealed record ParameterDefinition(string Name, string DisplayName, ParameterType Type)
{
    public bool Required { get; init; }
    public string? Description { get; init; }
    public string? Group { get; init; }
    public string? DefaultJson { get; init; }
    public string? MinimumJson { get; init; }
    public string? MaximumJson { get; init; }
    public bool WholeNumber { get; init; }
    public int? MaximumLength { get; init; }
    public ImmutableArray<ParameterOption> Options { get; init; } = [];
    public ImmutableArray<string> ConnectionKinds { get; init; } = [];
}
public sealed record ParameterOption(string Value, string DisplayName);
/// <summary>只返回声明中的字段名和稳定代码，绝不回显提交值或未知属性名。</summary>
public sealed record ParameterIssue(string Name, string Code);

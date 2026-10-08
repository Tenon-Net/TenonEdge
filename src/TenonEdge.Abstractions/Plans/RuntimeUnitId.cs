using System.Text.RegularExpressions;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Plans;

/// <summary>
/// 运行单元的确定性标识（总设计 §7.4、评审决定 Q14）：由单元种类与业务标识组成，不含随机数或版本号。
/// 同一配置对象在不同版本的运行计划中得到同一个标识，单实例规则据此判断“同一单元”，以后也可以据此只重启有变化的单元。
/// <list type="bullet">
/// <item>种类：小写字母开头、由点分隔的若干段，每段为小写字母和数字，例如 input.modbus、output.mqtt。</item>
/// <item>业务标识：一段，或以 / 分隔的最多 4 段；每段符合 <see cref="BusinessKeys"/> 的规则（总设计 §6.5）。
/// 全网关唯一的标识可以单独使用，例如 connectionKey、routeKey；只在设备内唯一的标识须带上作用域，
/// 例如数据来源写作 deviceKey/inputKey（总设计 §10.2、§10.5）。</item>
/// </list>
/// </summary>
public sealed partial record RuntimeUnitId
{
    private const int MaximumLength = 64;

    private const int MaximumKeySegments = 4;

    private RuntimeUnitId(string kind, string key)
    {
        Kind = kind;
        Key = key;
    }

    public string Kind { get; }

    public string Key { get; }

    public static RuntimeUnitId Create(string kind, string key)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(key);
        if (kind.Length > MaximumLength || !KindPattern().IsMatch(kind))
        {
            throw new ArgumentException($"运行单元种类 {kind} 不符合格式：小写字母开头、由点分隔，每段为小写字母和数字，最长 {MaximumLength} 个字符。", nameof(kind));
        }

        var segments = key.Split('/');
        if (segments.Length > MaximumKeySegments || !segments.All(BusinessKeys.IsValid))
        {
            throw new ArgumentException(
                $"运行单元的业务标识 {key} 不符合格式：一段或以 / 分隔的最多 {MaximumKeySegments} 段，每段为 ASCII 字母、数字和 -、_、.，首字符为字母或数字，最长 {BusinessKeys.MaximumLength} 个字符。",
                nameof(key));
        }

        return new RuntimeUnitId(kind, key);
    }

    /// <summary>业务标识按 / 分成的各段，例如数据来源为 deviceKey 与 inputKey 两段。</summary>
    public IReadOnlyList<string> KeySegments => Key.Split('/');

    public override string ToString() => $"{Kind}/{Key}";

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();
}

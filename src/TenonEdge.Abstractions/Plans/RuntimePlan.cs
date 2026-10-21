using System.Collections.Immutable;

namespace TenonEdge.Abstractions.Plans;

/// <summary>
/// 不可变的运行计划（总设计 §3.2、§7.4）：Host 把有效快照编译为运行计划交给 Runtime，这是二者之间唯一的配置契约，
/// Runtime 不读取 Host 的配置实体。计划包含版本号、运行单元及输出诊断归属；各单元的冻结配置由对应工厂解析。
/// 运行单元按标识排序保存；同一标识只能出现一次，这是单实例规则的前提。
/// </summary>
public sealed class RuntimePlan
{
    public RuntimePlan(long revision, IEnumerable<RuntimeUnitPlan> units, IReadOnlyDictionary<RuntimeUnitId, RuntimeUnitId>? diagnosticObjects = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        ArgumentNullException.ThrowIfNull(units);

        var sorted = units
            .Select(unit => unit ?? throw new ArgumentException("运行单元不能为空。", nameof(units)))
            .OrderBy(unit => unit.Id.Kind, StringComparer.Ordinal)
            .ThenBy(unit => unit.Id.Key, StringComparer.Ordinal)
            .ToImmutableArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Id == sorted[i - 1].Id)
            {
                throw new ArgumentException($"运行计划中有重复的运行单元 {sorted[i].Id}：同一单元只能有一个实例。", nameof(units));
            }
        }

        var ids = sorted.Select(unit => unit.Id).ToHashSet();
        DiagnosticObjects = (diagnosticObjects ?? ImmutableDictionary<RuntimeUnitId, RuntimeUnitId>.Empty).ToImmutableDictionary();
        if (DiagnosticObjects.Any(pair => !ids.Contains(pair.Key) || !pair.Key.Kind.StartsWith("output.", StringComparison.Ordinal) ||
            pair.Value is null || pair.Value.Kind != pair.Key.Kind))
        { throw new ArgumentException("诊断对象映射只允许已存在的同类型输出路径。", nameof(diagnosticObjects)); }
        Revision = revision;
        Units = sorted;
    }

    /// <summary>生成该计划的配置版本号（revision），由 Host 分配，单调递增、不复用。</summary>
    public long Revision { get; }

    public ImmutableArray<RuntimeUnitPlan> Units { get; }

    /// <summary>纯诊断标签：多条编译路径可归属同一画布输出节点，共用单对象缓存预算；不改变运行单元身份或发送。</summary>
    public ImmutableDictionary<RuntimeUnitId, RuntimeUnitId> DiagnosticObjects { get; }
    public RuntimeUnitId DiagnosticObject(RuntimeUnitId unit) => DiagnosticObjects.GetValueOrDefault(unit, unit);
}

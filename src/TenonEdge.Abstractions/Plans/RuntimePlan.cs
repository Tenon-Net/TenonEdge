using System.Collections.Immutable;

namespace TenonEdge.Abstractions.Plans;

/// <summary>
/// 不可变的运行计划（总设计 §3.2、§7.4）：Host 把有效快照编译为运行计划交给 Runtime，这是二者之间唯一的配置契约，
/// Runtime 不读取 Host 的配置实体。本批只含版本号与运行单元，路径、连接与目标的组成随 T1—T3 的使用者补充。
/// 运行单元按标识排序保存；同一标识只能出现一次，这是单实例规则的前提。
/// </summary>
public sealed class RuntimePlan
{
    public RuntimePlan(long revision, IEnumerable<RuntimeUnitPlan> units)
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

        Revision = revision;
        Units = sorted;
    }

    /// <summary>生成该计划的配置版本号（revision），由 Host 分配，单调递增、不复用。</summary>
    public long Revision { get; }

    public ImmutableArray<RuntimeUnitPlan> Units { get; }
}

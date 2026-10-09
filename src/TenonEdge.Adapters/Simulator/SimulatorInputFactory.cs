using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Simulator;

/// <summary>
/// 模拟输入的工厂（GW-07，总设计 §11“至少一个模拟输入”）：无硬件也能体验和测试，可复现正常、缺字段、非法值及连接错误场景。
/// 配置见 <see cref="SimulatorSettings"/>；构建只解析配置，不做 I/O。
/// </summary>
public sealed class SimulatorInputFactory : IInputUnitFactory
{
    public const string UnitKind = "input.simulator";

    public string Kind => UnitKind;

    public static IReadOnlyDictionary<string, FieldType> MeterFields => SimulatorSequence.MeterFields;

    /// <summary>纯配置校验与字段描述；不构造或执行运行单元。</summary>
    public static IReadOnlyDictionary<string, FieldType> Describe(RuntimeUnitPlan unit, string deviceKey, string inputKey)
    {
        var settings = SimulatorSettings.Parse(unit, deviceKey, inputKey);
        return settings.Sample is { } sample
            ? sample.ToDictionary(field => field.Key, field => field.Value.Type, StringComparer.Ordinal)
            : MeterFields;
    }

    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(context);
        if (unit.Id.Kind != UnitKind)
        {
            throw new RuntimeUnitBuildException(unit.Id, $"单元种类应为 {UnitKind}。");
        }

        var settings = SimulatorSettings.Parse(unit, context.DeviceKey, context.InputKey);
        return new SimulatorInputUnit(unit.Id, settings, context);
    }
}

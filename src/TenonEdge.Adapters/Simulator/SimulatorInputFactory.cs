using TenonEdge.Abstractions.Contracts;
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

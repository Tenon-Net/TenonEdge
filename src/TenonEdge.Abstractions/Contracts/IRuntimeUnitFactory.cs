using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 运行单元工厂（总设计 §7.4 第 2 步“准备”、§11）：按运行计划中的单元构建实例，由运行时在“只构建、不启动”阶段调用。
/// <list type="bullet">
/// <item>构建只做本地计算：解析与校验配置、创建对象。不做网络 I/O（包括 DNS 解析），不启动采集或发送，不生成消息。</item>
/// <item>配置无效等结构性问题抛出 <see cref="RuntimeUnitBuildException"/>：调用方放弃该版本，正在运行的原配置不受影响。</item>
/// <item>工厂是进程级的无状态对象；每次调用返回新实例，同一单元标识的旧实例是否已退出由调用方按单实例规则判断。</item>
/// </list>
/// </summary>
public interface IRuntimeUnitFactory
{
    /// <summary>负责的单元种类，与 <see cref="RuntimeUnitId.Kind"/> 相同，例如 input.modbus。</summary>
    string Kind { get; }

    /// <summary>构建一个尚未启动的运行单元。</summary>
    IRuntimeUnit Build(RuntimeUnitPlan unit, RuntimeUnitContext context);
}

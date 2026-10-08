using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 输入适配器的工厂（总设计 §7.4 第 2 步“准备”、§11“工厂 + 实例”）：按运行计划中的输入单元构建实例，由运行时在“只构建、不启动”阶段调用。
/// <list type="bullet">
/// <item>输入单元的种类以 <c>input.</c> 开头，业务标识为 deviceKey/inputKey 两段（总设计 §10.2），运行时解析后放在上下文中。</item>
/// <item>构建只做本地计算：解析与校验配置、创建对象。不做网络 I/O（包括 DNS 解析），不启动采集，不生成消息。</item>
/// <item>配置无效等结构性问题抛出 <see cref="RuntimeUnitBuildException"/>：应用时调用方放弃该版本，正在运行的原配置不受影响；
/// 启动时加载有效版本遇到时，只停止该单元并告警（总设计 §7.4）。</item>
/// <item>工厂是进程级的无状态对象。每次调用返回新实例：单元故障后，监督者用同一计划与上下文重新构建。</item>
/// </list>
/// 输出单元（发送者）需要发送缓存与凭证解析，随 T1-04 的第一个输出另行定义工厂。
/// </summary>
public interface IInputUnitFactory
{
    /// <summary>负责的单元种类，与 <see cref="RuntimeUnitId.Kind"/> 相同，例如 input.simulator。</summary>
    string Kind { get; }

    /// <summary>构建一个尚未运行的输入单元。</summary>
    IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context);
}

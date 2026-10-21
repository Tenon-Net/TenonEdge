using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 输入适配器的工厂（总设计 §7.4 第 2 步“准备”、§11“工厂 + 实例”）：按运行计划中的输入单元构建实例，由运行时在“只构建、不启动”阶段调用。
/// <list type="bullet">
/// <item>输入单元的种类以 <c>input.</c> 开头，业务标识为 deviceKey/inputKey 两段（总设计 §10.2），运行时解析后放在上下文中。</item>
/// <item>构建只做本地计算：解析与校验配置、创建对象。不做网络 I/O（包括 DNS 解析），不启动采集，不生成消息。</item>
/// <item>配置无效等结构性问题抛出 <see cref="RuntimeUnitBuildException"/>：应用时调用方放弃该版本，正在运行的原配置不受影响；
/// 启动时加载有效版本遇到时，只停止该单元并告警（总设计 §7.4）。</item>
/// <item>默认工厂是进程级无状态对象；需要输入/输出共同绑定的协议由 IRuntimePlanAdapterProvider 创建计划范围工厂，不能将其注册为进程级可变单例。
/// 每次调用返回新单元实例：单元故障后，监督者用同一计划工厂与上下文重新构建。</item>
/// </list>
/// 输出目标定义由 <see cref="IOutputAdapterFactory"/> 构建，再由目标创建发送适配器；发送缓存与发送者生命周期由运行时管理。
/// </summary>
public interface IInputUnitFactory
{
    /// <summary>负责的单元种类，与 <see cref="RuntimeUnitId.Kind"/> 相同，例如 input.simulator。</summary>
    string Kind { get; }

    /// <summary>需要持久接管结果的输入；没有输出时运行时也须消费并返回明确拒绝，不能让确认永久等待。</summary>
    bool UsesDurableHandoff => false;

    /// <summary>必须共同停止/恢复的接收会话标识。默认每个输入独立；共享接收会话的协议返回稳定的组标识。
    /// 只读取静态配置，不构建实例、不解密凭证、不做 I/O；即使随后 Build 失败，仍保留可确定的分组。</summary>
    RuntimeUnitId RecoveryGroup(RuntimeUnitPlan unit) => unit.Id;

    /// <summary>构建一个尚未运行的输入单元。</summary>
    IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context);
}

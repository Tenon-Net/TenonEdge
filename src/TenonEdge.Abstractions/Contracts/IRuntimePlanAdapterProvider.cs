namespace TenonEdge.Abstractions.Contracts;

/// <summary>进程级无状态提供者：每次 Prepare 创建独立的协议工厂集合。
/// 创建只做本地装配，不联网、不取得连接租约、不修改运行中计划；失败单元仍由原有 Build 契约处理。</summary>
public interface IRuntimePlanAdapterProvider
{
    /// <summary>用于配置发现的静态输出传输种类；不创建计划工厂、会话或租约。
    /// 未声明的旧提供者仍可运行，但不会被当作可配置的新目标能力。</summary>
    IReadOnlyList<string> OutputKinds => [];
    RuntimePlanAdapters Create();
}

/// <summary>计划范围的工厂，不是请求作用域服务。先构建全部输入，再构建输出；重建单元沿用该计划的工厂。</summary>
public sealed record RuntimePlanAdapters(IReadOnlyList<IInputUnitFactory> Inputs, IReadOnlyList<IOutputAdapterFactory> Outputs);

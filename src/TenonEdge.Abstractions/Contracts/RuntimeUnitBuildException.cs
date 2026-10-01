using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 构建运行单元时发现的结构性问题，例如配置缺少必填项、取值越界、种类不匹配（总设计 §7.4 第 2 步）。
/// 调用方据此放弃整个版本，正在运行的原配置不中断。
/// </summary>
public sealed class RuntimeUnitBuildException : Exception
{
    public RuntimeUnitBuildException()
    {
    }

    public RuntimeUnitBuildException(string message)
        : base(message)
    {
    }

    public RuntimeUnitBuildException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public RuntimeUnitBuildException(RuntimeUnitId unitId, string message, Exception? innerException = null)
        : base($"{unitId}：{message}", innerException)
    {
        UnitId = unitId;
    }

    /// <summary>出问题的单元；使用不带单元的构造函数时为空。</summary>
    public RuntimeUnitId? UnitId { get; }
}

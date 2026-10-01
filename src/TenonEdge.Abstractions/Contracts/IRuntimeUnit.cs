using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 已构建的运行单元：一个连接或输入的循环、发送者等（总设计 §7.4、§8.1）。
/// <list type="bullet">
/// <item>激活边界：构建之后、<see cref="StartAsync"/> 之前不做任何外部 I/O，也不生成消息。</item>
/// <item>单实例规则：同一单元标识同一时刻最多一个实例存活。<see cref="StopAsync"/> 因超时没有完成时，单元仍视为存活，
/// 调用方不得为同一标识启动新实例，直到它的循环退出。</item>
/// <item>资源由单元自己释放；向连接管理借用的共享连接只归还租约，不由单元释放（T2-01）。</item>
/// </list>
/// </summary>
public interface IRuntimeUnit : IAsyncDisposable
{
    RuntimeUnitId Id { get; }

    /// <summary>
    /// 启动循环后立即返回。不做阻塞式网络 I/O：连接在循环中建立，失败在循环中处理并上报，不构成启动失败（总设计 §7.4、§11）。
    /// 每个实例只能启动一次；请求停止之后不能再启动。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 请求停止，并等待循环退出后返回。协议库不支持取消的阻塞读取，由单元释放连接来打断（总设计 §8.1）。
    /// 取消令牌表示调用方愿意等待的期限：到期时抛出 <see cref="OperationCanceledException"/>，此时单元仍视为存活。
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}

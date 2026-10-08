using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 已构建的运行单元：一个连接或输入的循环、发送缓存写者等（总设计 §7.4、§8.1、§11）。单元只实现循环本身，
/// 启动、停止、异常捕获与重启都由运行时的监督者负责：
/// <list type="bullet">
/// <item>激活边界：构建之后、监督者调用 <see cref="RunAsync"/> 之前，不做任何外部 I/O，也不生成消息。</item>
/// <item>监督者在线程池线程上调用 <see cref="RunAsync"/>，每个实例只调用一次。连接在循环中建立，
/// 连接失败可以在循环内重试并上报，也可以抛出异常，由监督者记录最后错误、按退避构建新实例再运行（总设计 §7.4 第 5 步、§8.1）。</item>
/// <item>停止：监督者取消 <c>stoppingToken</c>，等待 <see cref="RunAsync"/> 返回，期限到时单元仍视为存活，
/// 同一单元标识不会创建新实例，直到它返回（单实例规则）。协议库不支持取消的阻塞读取，由单元在取消回调中释放连接来打断（总设计 §8.1）。
/// 单元在 <c>stoppingToken</c> 上注册的回调须在 <see cref="RunAsync"/> 返回之前释放（例如 <c>await using</c>）：
/// 同一单元的各个实例共用一个停止令牌，故障实例遗留的注册会一直挂在上面、随重启累积，到停止时才执行，
/// 可能访问已经释放的资源，或与正在进行的释放同时执行。</item>
/// <item>资源由单元自己释放：<see cref="RunAsync"/> 返回之后，监督者调用 <see cref="IAsyncDisposable.DisposeAsync"/>。
/// 向连接管理借用的共享连接只归还租约，不由单元释放（T2-01）。</item>
/// </list>
/// </summary>
public interface IRuntimeUnit : IAsyncDisposable
{
    RuntimeUnitId Id { get; }

    /// <summary>
    /// 运行单元的循环，直到 <paramref name="stoppingToken"/> 被取消。取消之后正常返回或抛出 <see cref="OperationCanceledException"/> 都视为正常停止；
    /// 没有请求停止就返回，或抛出其他异常，都是单元故障。调用时令牌已被取消的，应立即返回，不做任何外部 I/O。
    /// </summary>
    Task RunAsync(CancellationToken stoppingToken);
}

using System.Threading.Channels;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 构建运行单元时由运行时提供的上下文。只含进程级、线程安全的对象：不含请求作用域的服务、HttpContext、数据库上下文或事务，
/// 所以在一次管理请求中构建的单元，请求结束、作用域释放之后仍可启动和运行（后端实施规范 §4）。
/// 连接借用、凭证解析、计数与运行参数等只读接口随 T1、T2 的使用者补充。
/// </summary>
public sealed class RuntimeUnitContext
{
    public RuntimeUnitContext(TimeProvider time, ChannelWriter<EdgeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(messages);
        Time = time;
        Messages = messages;
    }

    /// <summary>周期、超时与退避使用的时钟（总设计 §8.1：注入 TimeProvider，便于测试）。</summary>
    public TimeProvider Time { get; }

    /// <summary>输入单元写入消息的有界通道（每个输入一个，满时等待，总设计 §8.1、§8.7）。</summary>
    public ChannelWriter<EdgeMessage> Messages { get; }
}

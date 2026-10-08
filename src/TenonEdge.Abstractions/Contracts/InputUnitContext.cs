using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 构建输入单元时由运行时提供的上下文。只含进程级、线程安全的对象：不含请求作用域的服务、HttpContext、数据库上下文或事务，
/// 所以构建出的单元在请求结束、作用域释放之后仍可运行（后端实施规范 §4）。单元故障后重新构建时沿用同一个上下文，
/// 通道中尚未取走的消息与读取状态不因重启丢失。连接借用、凭证解析等只读接口随 T2 的使用者补充。
/// </summary>
public sealed class InputUnitContext
{
    private readonly IClockSyncStatus _clock;

    public InputUnitContext(
        string deviceKey,
        string inputKey,
        TimeProvider time,
        IClockSyncStatus clock,
        IInputReadReporter reads,
        InputMessageWriter messages)
    {
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        BusinessKeys.Validate(inputKey, nameof(inputKey));
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(messages);
        DeviceKey = deviceKey;
        InputKey = inputKey;
        Time = time;
        _clock = clock;
        Reads = reads;
        Messages = messages;
    }

    /// <summary>逻辑设备标识，取自单元业务标识的第一段。</summary>
    public string DeviceKey { get; }

    /// <summary>设备内唯一的数据来源标识，取自单元业务标识的第二段，写入消息的 InputId。</summary>
    public string InputKey { get; }

    /// <summary>周期、超时与退避使用的时钟（总设计 §8.1：注入 TimeProvider，便于测试）。</summary>
    public TimeProvider Time { get; }

    /// <summary>
    /// 采集时间取网关时钟时的时间来源（总设计 §6.4、§8.1）：系统时钟未同步时为 gateway-unsynced，否则为 gateway。
    /// 每次读取时按当时的时钟状态取值。
    /// </summary>
    /// <summary>上游未来时间校验只在明确已同步时启用；未知与未同步不能合并成已同步。</summary>
    public ClockSyncState ClockState => _clock.State;

    public TimeSource GatewayTimeSource =>
        _clock.State == ClockSyncState.NotSynchronized ? TimeSource.GatewayUnsynced : TimeSource.Gateway;

    /// <summary>读取结果的上报，运行时据此维护设备状态（功能需求 §7.2）。</summary>
    public IInputReadReporter Reads { get; }

    /// <summary>
    /// 本输入的有界通道（总设计 §8.1、§8.7）。满时轮询类输入等待，到本周期截止仍无空位则跳过本周期并上报 overrun，
    /// 不在单元内另建缓冲。
    /// </summary>
    public InputMessageWriter Messages { get; }
}

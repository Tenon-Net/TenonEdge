using System.Collections.Immutable;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 构建输入单元时由运行时提供的上下文。只含进程级、线程安全的对象：不含请求作用域的服务、HttpContext、数据库上下文或事务，
/// 所以构建出的单元在请求结束、作用域释放之后仍可运行（后端实施规范 §4）。单元故障后重新构建时沿用同一个上下文，
/// 通道中尚未取走的消息、读取状态与冻结连接不因重启丢失。活跃连接和凭证不进入上下文。
/// </summary>
public sealed class InputUnitContext
{
    private readonly IClockSyncStatus _clock;
    private readonly Task _activation;

    public InputUnitContext(
        string deviceKey,
        string inputKey,
        TimeProvider time,
        IClockSyncStatus clock,
        IInputReadReporter reads,
        InputMessageWriter messages,
        Task? activation = null,
        IInputBufferBudget? bufferBudget = null,
        IReadOnlyDictionary<string, ModuleConnectionSnapshot>? connections = null)
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
        BufferBudget = bufferBudget;
        Connections = connections?.ToImmutableDictionary(StringComparer.Ordinal) ?? ImmutableDictionary<string, ModuleConnectionSnapshot>.Empty;
        _activation = activation ?? Task.CompletedTask;
    }

    /// <summary>共享连接类输入在联网前等待本计划完成启动安排。准备阶段不放行；
    /// 草稿被放弃或激活失败时取消。单元自身停止也应结束等待，不能取消其他单元的屏障。
    /// 独立装配未指定 activation 时直接放行。</summary>
    public Task WaitForActivationAsync(CancellationToken cancellationToken) => _activation.WaitAsync(cancellationToken);

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

    /// <summary>协议正文分配前的进程共享缓冲预算；未启用时为 null。</summary>
    public IInputBufferBudget? BufferBudget { get; }

    /// <summary>按参数名索引的冻结连接。监督重建沿用本上下文，不从当前草稿重新解析。
    /// 凭证仍按 ConnectionId 经原受保护解析器在使用时获取。</summary>
    public ImmutableDictionary<string, ModuleConnectionSnapshot> Connections { get; }
}

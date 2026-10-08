namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 输入单元上报每个周期的读取结果（总设计 §6.1、§8.1，功能需求 §7.2）。运行时据此维护设备状态：
/// 最近一个周期全部成功为“正常”，有字段失败为“部分失败”，连续 3 个周期整体失败为“离线”；最后成功时间与最后错误供诊断显示。
/// 可以在任意线程上调用，通常立即返回；只有状态转换（判为离线、恢复读取）时同步写一条运行事件（一次配置库写入），
/// 单元不应在持有锁或需要精确计时的位置调用。说明中不得包含报文正文、点位的值和秘密（总设计 §3.3）。
/// </summary>
public interface IInputReadReporter
{
    /// <summary>一次读取完成并生成了消息；<paramref name="failedFieldCount"/> 为其中标为 bad 的字段数，0 表示全部成功。</summary>
    void ReadCompleted(DateTimeOffset completedAt, int failedFieldCount);

    /// <summary>整组读取失败（例如连接中断）：不生成消息，不伪造正常数据（总设计 §6.1）。</summary>
    void ReadFailed(DateTimeOffset completedAt, string reason);

    /// <summary>本周期因通道满到截止仍无空位而跳过，计一次 overrun（总设计 §8.1、§8.5）。</summary>
    void CycleSkipped(DateTimeOffset skippedAt);
}

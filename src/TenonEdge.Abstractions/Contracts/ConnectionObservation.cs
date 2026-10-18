namespace TenonEdge.Abstractions.Contracts;

public enum ConnectionCommunicationState { Connecting, Connected, Degraded }

/// <summary>当前会话的非秘密通信事实，不代表设备在线或业务配置已应用。成功后保留最后错误及其时间；
/// 错误码只能是适配器定义的固定分类，不能包含异常文本、响应正文、地址或凭证。重建会话后重新观测。</summary>
public sealed record ConnectionObservation(ConnectionCommunicationState State, DateTimeOffset? LastSuccessAt = null,
    string? LastErrorCode = null, DateTimeOffset? LastErrorAt = null);

/// <summary>适配器提供线程安全、无 I/O 的不可变快照；查询不等待串行执行器，也不触发重连或读取凭证。</summary>
public interface IConnectionObservationSource
{
    ConnectionObservation Observation { get; }
}

/// <summary>关闭期间仍保留原会话所有权；不支持观测的扩展返回空 Observation，不推定连接成功。</summary>
public sealed record SharedConnectionObservation(bool Closing, ConnectionObservation? Observation);

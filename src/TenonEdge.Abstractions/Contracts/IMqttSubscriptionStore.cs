namespace TenonEdge.Abstractions.Contracts;

/// <summary>Broker 的持久会话身份；不含密码，订阅不按计划版本隔离。</summary>
public sealed record MqttSessionIdentity(string Recipient, string ClientId);

/// <summary>Confirmed 仅表示 SUBACK 已收到且已落盘；未确认项可能已在 Broker 生效。</summary>
public sealed record MqttSubscriptionState(string Filter, int Qos, bool Confirmed);

/// <summary>
/// 持久订阅日志。每次操作独立提交，不跨网络持有事务；同一会话由共享连接执行器串行调用。
/// SUBSCRIBE 前写未确认项，SUBACK 后写确认项；UNSUBACK 后才能删除。
/// 网络与本地存储不能原子提交，中断后的未确认项须重新协调，可能重复收到保留消息。
/// </summary>
public interface IMqttSubscriptionStore
{
    IReadOnlyList<MqttSubscriptionState> Read(MqttSessionIdentity session);
    void Put(MqttSessionIdentity session, MqttSubscriptionState subscription);
    void Remove(MqttSessionIdentity session, string filter);
    void Clear(MqttSessionIdentity session);
}

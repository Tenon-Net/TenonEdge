using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Diagnostics;

/// <summary>只保存一个有界不可变快照。轮询结果与 MQTT 事件使用同一时间/错误保留规则；不拥有连接或重试循环。</summary>
internal sealed class ConnectionObservationTracker(TimeProvider time)
{
    private readonly Lock _lock = new();
    private ConnectionObservation _value = new(ConnectionCommunicationState.Connecting);
    internal ConnectionObservation Snapshot => Volatile.Read(ref _value);

    internal void Succeeded()
    {
        lock (_lock)
        { Volatile.Write(ref _value, _value with { State = ConnectionCommunicationState.Connected, LastSuccessAt = time.GetUtcNow() }); }
    }

    internal void Failed(string code)
    {
        lock (_lock)
        { Volatile.Write(ref _value, _value with { State = ConnectionCommunicationState.Degraded, LastErrorCode = code, LastErrorAt = time.GetUtcNow() }); }
    }

    internal void Disconnected()
    {
        lock (_lock)
        { Volatile.Write(ref _value, _value with { State = ConnectionCommunicationState.Connecting }); }
    }
}

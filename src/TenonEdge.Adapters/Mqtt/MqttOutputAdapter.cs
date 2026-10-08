using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>输出只借用共享会话；总期限包含等待串行执行器，不再自行创建或拥有 MQTT 客户端。</summary>
internal sealed class MqttOutputAdapter(IConnectionLease<MqttSession> connection, TimeProvider time, int timeoutMs) : IOutputAdapter
{
    public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            return await connection.ExecuteAsync((session, token) => session.SendAsync(message, token), operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(OutputSendStatus.Retry, "MQTT 排队或网络操作超时，记录保持待发。");
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

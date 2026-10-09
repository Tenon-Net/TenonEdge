using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Modbus;

/// <summary>借用正式串行会话，仅测传输；新会话不伪造启用 UnitId。总期限含排队。</summary>
public sealed class ModbusConnectionTest(ISharedConnections connections, IOutboundConnector connector, TimeProvider time)
{
    public async Task<ModbusConnectionTestResult> RunAsync(ModbusConnectionSettings settings, CancellationToken cancellationToken)
    {
        settings = settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        var budget = TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs);
        using var deadline = new CancellationTokenSource(budget, time);
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        IConnectionLease<ModbusConnectionSession> lease;
        try
        { lease = connections.Acquire(settings.ConnectionId, "modbus", settings.Identity, () => new ModbusConnectionSession(settings, null, connector, time)); }
        catch (InvalidOperationException) { throw new ModbusConnectionUnavailableException(); }
        await using var ownership = lease;
        try
        {
            var result = await lease.ExecuteAsync((session, token) =>
            {
                token.ThrowIfCancellationRequested();
                var remaining = budget - time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                { throw new OperationCanceledException(queued.Token); }
                return session.TestConnectionAsync(cancellationToken, remaining);
            }, queued.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return result with { ElapsedMs = time.GetElapsedTime(started).TotalMilliseconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "connection.busy", time.GetElapsedTime(started).TotalMilliseconds,
                new ConnectionTestTrace(time, false).FinishModbus("connection.busy", false));
        }
    }
}

public sealed class ModbusConnectionUnavailableException : Exception
{
    public ModbusConnectionUnavailableException() : base("共享 Modbus 会话不能借用。") { }
}

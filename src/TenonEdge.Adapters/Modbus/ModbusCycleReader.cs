using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Modbus;

internal sealed record ModbusCycleResult(bool Overrun, ImmutableArray<ModbusReadResult> Blocks)
{
    internal bool HasSuccessfulRead => !Overrun && Blocks.Any(block => block.Outcome == ModbusRequestOutcome.Success);
    internal bool HasActualRequest => !Overrun && Blocks.Any(block => block.WasRequested);
}

/// <summary>一次 UnitId 采集周期，不持有连接或上周期的值。
/// 调用者提供本周期剩余时间（包括排队和请求间隔）；每块通过既有共享租约准入。
/// 周期超时丢弃整周期结果，停止取消向上传播；两者都等待底层真实 I/O 退出。
/// 本类不创建循环或补采，调度层负责正常周期、跳过计数和同设备多组协调。</summary>
internal static class ModbusCycleReader
{
    internal static async Task<ModbusCycleResult> ReadAsync(IConnectionLease<ModbusConnectionSession> lease,
        byte unit, ImmutableArray<ModbusReadBlock> blocks, TimeSpan remaining, TimeProvider time, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(time);
        if (blocks.IsDefaultOrEmpty)
        { throw new ArgumentException("Modbus 周期须有读取块。", nameof(blocks)); }
        foreach (var block in blocks)
        {
            if (block.FunctionCode is < 1 or > 4 || block.Count == 0 ||
                block.Count > (block.FunctionCode <= 2 ? 2000 : 125) || (int)block.Start + block.Count > 65536)
            { throw new ArgumentException("Modbus 周期读取块无效。", nameof(blocks)); }
        }
        stoppingToken.ThrowIfCancellationRequested();
        if (remaining <= TimeSpan.Zero)
        { return new(true, []); }
        using var deadline = new CancellationTokenSource(remaining, time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        var results = ImmutableArray.CreateBuilder<ModbusReadResult>(blocks.Length);
        ModbusRequestOutcome? fastFailure = null;
        try
        {
            foreach (var block in blocks)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (fastFailure is { } reason)
                {
                    // 只标结果，不调用连接健康 Record；未发送的块不是新一次从站超时事实。
                    results.Add(new(reason, false));
                    continue;
                }
                var result = await lease.ExecuteAsync((session, token) =>
                    session.ReadAsync(unit, block.FunctionCode, block.Start, block.Count, token), operation.Token);
                results.Add(result);
                if (result.Outcome is ModbusRequestOutcome.Timeout or ModbusRequestOutcome.ConnectionError)
                { fastFailure = result.Outcome; }
            }
            operation.Token.ThrowIfCancellationRequested();
            return new(false, results.MoveToImmutable());
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { return new(true, []); }
    }
}

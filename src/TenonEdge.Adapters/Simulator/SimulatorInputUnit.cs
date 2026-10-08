using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Simulator;

/// <summary>
/// 模拟输入的运行单元（GW-07）：按固定周期“读取”一次，行为与轮询类输入一致（总设计 §6.1、§8.1）：
/// <list type="bullet">
/// <item>周期由一个固定周期的计时器给出（第 n 个周期在启动后 n × 周期开始），不随处理时间漂移。
/// 落后超过一个周期时（例如进程被暂停），计时器把错过的周期合并为一次，按单调时钟补计被跳过的周期（overrun）。</item>
/// <item>读取成功生成一条消息：编号为 UUIDv7，采集时间等于接收时间（读取完成的时刻），时间来源按时钟同步状态取 gateway 或 gateway-unsynced。</item>
/// <item>通道满时等待，到下一个周期开始仍无空位则丢弃本条并上报 overrun（总设计 §8.1），不在单元内另建缓冲。</item>
/// <item>连接错误时整组读取失败：不生成消息，上报失败原因，由运行时判定离线。</item>
/// </list>
/// 不做任何网络 I/O；停止令牌取消后在当前等待处返回，停止时不计 overrun。
/// </summary>
internal sealed class SimulatorInputUnit(RuntimeUnitId id, SimulatorSettings settings, InputUnitContext context) : IRuntimeUnit
{
    public const string ConnectionErrorReason = "模拟的连接错误。";

    public RuntimeUnitId Id { get; } = id;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var time = context.Time;
        var period = settings.Period;
        var sequence = new SimulatorSequence(settings);
        using var timer = new PeriodicTimer(period, time);
        var startedAt = time.GetTimestamp();
        long cycle = 0;
        try
        {
            while (true)
            {
                var nextCycleStarted = await ReadOnceAsync(sequence, timer, stoppingToken);
                if (!nextCycleStarted && !await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                cycle++;
                var due = (long)(time.GetElapsedTime(startedAt) / period);
                for (; cycle < due; cycle++)
                {
                    context.Reads.CycleSkipped(time.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止：通道中尚未取走的消息属于进程内存中的数据（总设计 §8.5）。
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>读取一次并写入通道。返回 true 表示等待空位时已经到了下一个周期（计时器的这一次已被消耗）。</summary>
    private async Task<bool> ReadOnceAsync(SimulatorSequence sequence, PeriodicTimer timer, CancellationToken stoppingToken)
    {
        var reading = sequence.Next();
        var completedAt = context.Time.GetUtcNow();
        if (reading.IsConnectionError)
        {
            context.Reads.ReadFailed(completedAt, ConnectionErrorReason);
            return false;
        }

        var message = new EdgeMessage(
            Guid.CreateVersion7(completedAt),
            context.InputKey,
            context.DeviceKey,
            completedAt,
            context.GatewayTimeSource,
            completedAt,
            reading.Values,
            reading.Quality);
        context.Reads.ReadCompleted(completedAt, reading.Quality.Count);
        if (context.Messages.TryWrite(message))
        {
            return false;
        }

        // 通道满：等空位，最多等到下一个周期开始。
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var write = context.Messages.WriteAsync(message, giveUp.Token).AsTask();
        var nextCycle = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        if (await Task.WhenAny(write, nextCycle) == write && write.IsCompletedSuccessfully)
        {
            await nextCycle;
            return true;
        }

        await giveUp.CancelAsync();
        var written = await CompletedAsync(write);
        await nextCycle;
        if (!written)
        {
            context.Reads.CycleSkipped(context.Time.GetUtcNow());
        }

        return true;
    }

    private static async Task<bool> CompletedAsync(Task write)
    {
        try
        {
            await write;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

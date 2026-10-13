using System.Collections.Immutable;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

public sealed partial class RuntimeHost
{
    // 只在生命周期操作门内改动。状态跨监督者重建和版本交接沿用，绝不由请求的 using/finally 释放。
    private readonly Dictionary<long, OutputSenderState.QueuePause> _applicationSenderPauses = [];

    private async Task<ImmutableArray<RuntimeUnitId>> PauseApplicationSendersAsync(long[] selected, List<long> added)
    {
        if (selected.Length == 0)
        { return []; }
        PreparedOutput[] senders;
        lock (_lock)
        {
            if (_activeRevision is null || selected.Any(id => !_senders.ContainsKey(id)) ||
                !_supervisor.Snapshot().Any(unit => unit.Id == DeliveryCache.WriterId && unit.State == RuntimeUnitState.Running))
            { throw new InvalidOperationException("迁移需要当前有效发送者及运行中的队列写者。"); }
            senders = selected.Where(id => !_applicationSenderPauses.ContainsKey(id)).Select(id => _senders[id]).ToArray();
            if (senders.Any(sender => sender.Target.SendTimeout <= TimeSpan.Zero || sender.Target.SendTimeout > TimeSpan.FromSeconds(60)))
            { throw new InvalidOperationException("迁移发送超时配置无效。"); }
        }
        // 在停止共享输入/发送循环之前请求屏障；否则已退出的循环永远不能确认暂停，未提交的 ACK 也可能遗留。
        // 所有连接先一起暂停，再并行等待各自的发送期限，不能累加成连接数 × 超时。
        foreach (var sender in senders)
        {
            var id = sender.Target.ConnectionId;
            _applicationSenderPauses.Add(id, sender.State.PauseQueue());
            added.Add(id);
        }
        var results = await Task.WhenAll(senders.Select(async sender =>
        {
            try
            {
                await _applicationSenderPauses[sender.Target.ConnectionId].Reached.Task
                    .WaitAsync(sender.Target.SendTimeout, _time, CancellationToken.None);
                return (RuntimeUnitId?)null;
            }
            catch (TimeoutException) { return sender.Target.Id; }
        }));
        return [.. results.OfType<RuntimeUnitId>()];
    }

    private void ReleaseApplicationSenderPauses(IEnumerable<long>? selected = null)
    {
        foreach (var id in selected ?? _applicationSenderPauses.Keys.ToArray())
        {
            if (_applicationSenderPauses.Remove(id, out var pause))
            { pause.Dispose(); }
        }
    }
}

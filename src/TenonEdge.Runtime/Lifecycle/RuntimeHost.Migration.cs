using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

public sealed partial class RuntimeHost
{
    /// <summary>宿主已确认有效配置、每条原记录的同协议与所有者声明，并持久登记迁移开始。
    /// 原源/目标发送者都提交在途结果后，才交给唯一写者；不取消/替换发送者，不解除暂停。
    /// 本入口只支持已有目标发送者；无输出路线目标的接续由宿主另行准备，不能把不存在的发送者当作已暂停。
    /// 接受后由运行时拥有，调用方取消仅结束等待；停机等待同一操作完成。</summary>
    public Task<DeliveryWriteResult> MigrateDeliveriesAsync(DeliveryMigrationCommand command, long revision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        token.ThrowIfCancellationRequested();
        var operation = EnterOperation();
        return MigrateDeliveriesOwnedAsync(command, revision, operation).WaitAsync(token);
    }

    private async Task<DeliveryWriteResult> MigrateDeliveriesOwnedAsync(DeliveryMigrationCommand command, long revision, TaskCompletionSource operation)
    {
        List<(OutputSenderState State, OutputSenderState.QueuePause Pause)> pauses = [];
        try
        {
            PreparedOutput target;
            PreparedOutput? source;
            TimeSpan timeout;
            lock (_lock)
            {
                if (_activeRevision != revision || _quiescenceRequested ||
                    !_senders.TryGetValue(command.TargetConnectionId, out target!) ||
                    !_supervisor.Snapshot().Any(unit => unit.Id == DeliveryCache.WriterId && unit.State == RuntimeUnitState.Running))
                { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.Unavailable); }
                _senders.TryGetValue(command.SourceConnectionId, out source);
                if (source is null && (_supervisor.IsGroupOccupied(SenderGroup(command.SourceConnectionId)) || _senderStates.ContainsKey(command.SourceConnectionId)))
                { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.Unavailable); }
                if (target.Target.Id.Kind is not ("output.http" or "output.mqtt") ||
                    source is not null && source.Target.Id.Kind != target.Target.Id.Kind)
                { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.ProtocolMismatch); }
                if (target.Target.CurrentFingerprint != command.Fingerprint)
                { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.TargetChanged); }
                timeout = source is null || source.Target.SendTimeout < target.Target.SendTimeout ? target.Target.SendTimeout : source.Target.SendTimeout;
                if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
                { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.Unavailable); }
            }
            // 同一连接只申请一次；两个不同连接先一起发信号，不能暂停一个、等待它后才阻止另一个取新记录。
            pauses.Add((target.State, target.State.PauseQueue()));
            if (source is not null && source.Target.ConnectionId != target.Target.ConnectionId)
            { pauses.Add((source.State, source.State.PauseQueue())); }
            try
            { await Task.WhenAll(pauses.Select(item => item.Pause.Reached.Task)).WaitAsync(timeout, _time, CancellationToken.None); }
            catch (TimeoutException)
            { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.NotQuiescent); }
            if (target.Target.CurrentFingerprint != command.Fingerprint)
            { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.TargetChanged); }
            var result = await _deliveries.MigrateAsync(command);
            if (result.Committed)
            {
                // 迁移不改变 seq，却会清零记录的失败次数。不能带着旧代次的“刚探测成功，复查毒队头”继续判断。
                foreach (var pause in pauses)
                { pause.State.RequestQueueProbeReset(); }
            }
            return result;
        }
        finally
        {
            foreach (var pause in pauses)
            { pause.Pause.Dispose(); }
            CompleteOperation(operation);
        }
    }
}

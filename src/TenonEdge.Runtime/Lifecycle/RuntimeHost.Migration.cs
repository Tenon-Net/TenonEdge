using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

public sealed partial class RuntimeHost
{
    /// <summary>准备阶段只读取原发送者的同次非秘密身份；不创建客户端、不发送、不把当前草稿当成有效目标。</summary>
    public OutputTargetIdentity? CaptureMigrationTarget(long connectionId, long revision, RuntimeQuiescenceResult? quiet = null)
    {
        lock (_lock)
        {
            if (_stopping is not null || _operation is not null || _activeRevision != revision ||
                (quiet is null ? _quiescenceRequested : !ReferenceEquals(_currentQuiescence, quiet) || quiet.Revision != revision ||
                    !_applicationSenderPauses.ContainsKey(connectionId)) ||
                !_senders.TryGetValue(connectionId, out var sender) || sender.Target.Id.Kind is not ("output.http" or "output.mqtt"))
            { return null; }
            var identity = sender.Target.CaptureIdentity();
            return identity is not null && sender.Target.Id.Kind == "output." + identity.Recipient.Protocol ? identity : null;
        }
    }

    /// <summary>宿主已确认有效配置、每条原记录的同协议与所有者声明，并持久登记迁移开始。
    /// 原源/目标发送者都提交在途结果后，才交给唯一写者；不取消/替换发送者，不解除暂停。
    /// 本入口只支持已有目标发送者；宿主计划为无输出路线的启用连接准备queue-only单元，不在迁移中另建发送者。
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
                if (!MigrationIdentityMatches(target.Target, command))
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
            if (!MigrationIdentityMatches(target.Target, command))
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

    private static bool MigrationIdentityMatches(IOutputTarget target, DeliveryMigrationCommand command)
    {
        if (command.Hold is null)
        { return target.CurrentFingerprint == command.Fingerprint; }
        // 意图核对使用同一次凭证描述，不能拼接轮换前的指纹与轮换后的可读身份。
        var identity = target.CaptureIdentity();
        return identity?.Fingerprint == command.Fingerprint && identity.Summary == command.Summary;
    }

}

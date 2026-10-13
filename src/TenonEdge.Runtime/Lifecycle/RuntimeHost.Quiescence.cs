using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>只描述输入静默结果，不代表配置已提交或已恢复。失败时不得进入提交。
/// 超时实例仍由原监督者持有，不能因本结果返回就构造第二个运行实例。</summary>
public sealed record RuntimeQuiescenceResult(long? Revision, bool Drained, ImmutableArray<RuntimeUnitId> TimedOutUnits)
{
    public bool Succeeded => Drained && TimedOutUnits.IsEmpty;
}

public sealed partial class RuntimeHost
{
    /// <summary>应用第 3 步：停止全部输入，排空旧版本分发，释放与输入共用会话的发送者。
    /// 写者、独立发送者、有效版本及队列均保持不变；不提交配置、不自动恢复输入。
    /// 超时保留原实例和预算，不丢弃未完成分发。开始后操作由运行时拥有，调用方取消等待不打断清理。
    /// 应用所有者必须在失败时恢复旧输入，成功后再提交/激活；此方法不是独立应用入口。</summary>
    public Task<RuntimeQuiescenceResult> QuiesceInputsAsync(CancellationToken cancellationToken)
        => QuiesceInputsAsync([], cancellationToken);

    /// <summary>形式 A 的提交前屏障：先等待选中连接的原发送结果持久化，再静默输入并排空。
    /// 成功后发送屏障由运行时持有，不因调用方取消或方法返回释放；提交前放弃时调用 RestoreInputsAsync。
    /// 提交后须由协调者持久隔离等待迁移的记录，再调用 ActivateCommittedAsync 交接。
    /// 本方法不登记迁移意图、不改变队列状态、不证明配置提交，也不代替管理员授权和配额/身份复核。</summary>
    public Task<RuntimeQuiescenceResult> QuiesceInputsAsync(IReadOnlyCollection<long> migrationConnections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrationConnections);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = migrationConnections.ToArray();
        if (selected.Length > 500 || selected.Any(id => id is < 1 or > 9007199254740991L) || selected.Distinct().Count() != selected.Length)
        { throw new ArgumentException("迁移连接清单无效。", nameof(migrationConnections)); }
        var operation = EnterOperation();
        var task = QuiesceOwnedAsync(operation, selected);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<RuntimeQuiescenceResult> QuiesceOwnedAsync(TaskCompletionSource operation, long[] migrationConnections)
    {
        var added = new List<long>();
        var succeeded = false;
        try
        {
            lock (_lock)
            { _currentQuiescence = null; }
            await CancelRecoveryAsync();
            var blocked = await PauseApplicationSendersAsync(migrationConnections, added);
            if (blocked.Length != 0)
            { return new(_activeRevision, false, blocked); }
            var result = await QuiesceCoreAsync();
            succeeded = result.Succeeded;
            lock (_lock)
            {
                if (_stopping is null && result.Succeeded && result.Revision is not null)
                { _currentQuiescence = result; }
            }
            return result;
        }
        finally
        {
            // 失败的新请求不留下不可见暂停。先前成功屏障仍归原应用所有，不能因后一次重试失败而释放。
            if (!succeeded)
            { ReleaseApplicationSenderPauses(added); }
            CompleteOperation(operation);
        }
    }

    private bool _quiescenceRequested;
    private RuntimeQuiescenceResult? _currentQuiescence;

    /// <summary>核对本运行时最近一次完整静默的结果。恢复、新静默或停机会使旧结果失效；
    /// Succeeded 只描述当时的结果，不能单独作为提交前置条件。
    /// 此查询不持有应用锁、不证明配置已提交；宿主仍须串行拥有完整应用/回滚操作。</summary>
    public bool IsCurrentQuiescence(RuntimeQuiescenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            return _stopping is null && _operation is null && ReferenceEquals(_currentQuiescence, result)
                && result.Revision == _activeRevision;
        }
    }

    private async Task<RuntimeQuiescenceResult> QuiesceCoreAsync()
    {
        try
        {
            _quiescenceRequested = _activeRevision is not null;
            _recoveryGroups.Clear();
            _waitingRecoveryGroups.Clear();
            _recoveryFailures.Clear();
            _recoveryGroups.UnionWith(_activeInputs.Values.Select(input => input.RecoveryGroup ?? input.Id));
            foreach (var output in _activeOutputs)
            { _recoverySenderStates[output.Target.ConnectionId] = output.State; }
            var timeout = _options.UnitStopTimeout;
            var retiring = await StopRetiringSendersAsync(null);
            if (retiring.Count != 0)
            { return new(_activeRevision, false, [.. retiring]); }
            var stopped = await _supervisor.StopAsync(id => id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal), timeout, CancellationToken.None);
            if (stopped.Count != 0)
            { return new(_activeRevision, false, [.. stopped]); }

            foreach (var dispatcher in _dispatchers)
            { dispatcher.Channel.Writer.TryComplete(); }
            try
            {
                await Task.WhenAll(_dispatchers.Select(dispatcher => dispatcher.State.Drained.Task)).WaitAsync(timeout, _time, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                _events.Record(RuntimeEventSeverity.Error, "runtime.quiesceDrainTimedOut", null,
                    "应用静默的旧版本消息尚未排空；保持写者、分发器和预算，不允许提交新版本。");
                return new(_activeRevision, false, []);
            }

            var dispatchers = await _supervisor.StopAsync(id => id.Kind == "delivery.dispatcher", timeout, CancellationToken.None);
            if (dispatchers.Count != 0)
            { return new(_activeRevision, true, [.. dispatchers]); }
            var coupled = _senders.Values.Where(output => output.Target.QuiesceWithInputs).Select(output => output.Target.Id).ToHashSet();
            var outputs = await _supervisor.StopAsync(coupled.Contains, timeout, CancellationToken.None);
            return new(_activeRevision, true, [.. outputs]);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 操作由运行时持有，即使调用方已取消等待，也在这里收敛故障；不把原始异常中的配置写入诊断。
            _events.Record(RuntimeEventSeverity.Error, "runtime.quiesceFailed", null,
                "输入静默未完成；保留旧版本与未退出实例，不允许提交新版本。");
            return new(_activeRevision, false, []);
        }
    }

    private TaskCompletionSource EnterOperation()
    {
        lock (_lock)
        {
            ThrowIfStopping();
            if (_operation is not null)
            { throw new InvalidOperationException("运行时已有生命周期操作正在执行。"); }
            _operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _operation;
        }
    }

    private void CompleteOperation(TaskCompletionSource operation)
    {
        lock (_lock)
        {
            _operation = null;
            operation.TrySetResult();
        }
    }
}

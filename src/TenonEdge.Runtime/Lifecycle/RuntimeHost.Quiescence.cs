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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = EnterOperation();
        var task = QuiesceOwnedAsync(operation);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<RuntimeQuiescenceResult> QuiesceOwnedAsync(TaskCompletionSource operation)
    {
        try
        {
            await CancelRecoveryAsync();
            return await QuiesceCoreAsync();
        }
        finally { CompleteOperation(operation); }
    }

    private bool _quiescenceRequested;

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
            var coupled = _activeOutputs.Where(output => output.Target.QuiesceWithInputs).Select(output => output.Target.Id).ToHashSet();
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

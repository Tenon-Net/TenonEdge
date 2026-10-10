using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>一次旧有效版本恢复尝试。超时组继续由运行时恢复；构建失败按单元显示，修复后可显式重试。</summary>
public sealed record RuntimeInputRecoveryResult(long? Revision, bool Restarted,
    RuntimeQuiescenceResult? BlockedBy, ImmutableArray<RuntimeUnitBuildFailure> Failures)
{
    public bool Succeeded => BlockedBy is null && Failures.IsEmpty;
}

public sealed partial class RuntimeHost
{
    private readonly HashSet<RuntimeUnitId> _recoveryGroups = [];
    private readonly HashSet<RuntimeUnitId> _waitingRecoveryGroups = [];
    private readonly Dictionary<RuntimeUnitId, ImmutableArray<RuntimeUnitBuildFailure>> _recoveryFailures = [];
    private readonly Dictionary<long, OutputSenderState> _recoverySenderStates = [];
    private Task? _recoveryWorker;
    private CancellationTokenSource? _recoveryCancellation;

    /// <summary>提交前放弃应用后，按原有效定义分组恢复输入。已完整排空的组先恢复；超时组由运行时继续等待和恢复。
    /// 共享接收会话中的全部输入、分发器和耦合发送者退出前，不构建该组新实例。独立输出和写者保持运行。
    /// 不接受请求取消；应用所有者不得把一次恢复尝试返回当成新配置可提交。构建失败修复后可再次调用。</summary>
    public async Task<RuntimeInputRecoveryResult> RestoreInputsAsync()
    {
        var operation = EnterOperation();
        try
        {
            lock (_lock)
            { _currentQuiescence = null; }
            await CancelRecoveryAsync();
            try
            { return await RecoverGroupsCoreAsync(onlyWaiting: false); }
            finally
            { StartRecoveryIfNeeded(); }
        }
        finally { CompleteOperation(operation); }
    }

    private void StartRecoveryIfNeeded()
    {
        if (_waitingRecoveryGroups.Count == 0)
        { return; }
        _recoveryCancellation = new CancellationTokenSource();
        var token = _recoveryCancellation.Token;
        _recoveryWorker = Task.Run(() => RecoverWaitingGroupsAsync(token));
    }

    // 仅在已拥有生命周期门或已开始停机时调用；后台恢复此时不能进入下一次尝试。
    private async Task CancelRecoveryAsync()
    {
        _recoveryCancellation?.Cancel();
        if (_recoveryWorker is not null)
        { await _recoveryWorker; }
        _recoveryCancellation?.Dispose();
        _recoveryCancellation = null;
        _recoveryWorker = null;
    }

    private async Task RecoverWaitingGroupsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            { await Task.Delay(TimeSpan.FromMilliseconds(250), _time, token); }
            catch (OperationCanceledException) { return; }
            TaskCompletionSource operation;
            lock (_lock)
            {
                if (_stopping is not null || token.IsCancellationRequested)
                { return; }
                if (_operation is not null)
                { continue; }
                operation = _operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                await RecoverGroupsCoreAsync(onlyWaiting: true);
                if (_waitingRecoveryGroups.Count == 0)
                { return; }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _events.Record(RuntimeEventSeverity.Error, "runtime.inputRecoveryFailed", null,
                    "原有效版本的输入恢复未完成；保留原实例及恢复待办，不允许据此提交新配置。");
            }
            finally { CompleteOperation(operation); }
        }
    }

    private async Task<RuntimeInputRecoveryResult> RecoverGroupsCoreAsync(bool onlyWaiting)
    {
        if (!_quiescenceRequested || _activeDefinition is null)
        { return new(_activeRevision, false, null, []); }
        var selected = (onlyWaiting ? _waitingRecoveryGroups : _recoveryGroups).ToHashSet();
        _waitingRecoveryGroups.UnionWith(selected);
        var groups = _activeInputs.Values.GroupBy(input => input.RecoveryGroup ?? input.Id)
            .Where(group => selected.Contains(group.Key)).Select(group => (Id: group.Key, Inputs: group.Select(input => input.Id).ToHashSet())).ToArray();
        // 组间并行静默，避免一个不可取消的实例让总等待变为“组数 × 超时”。不在此阶段改变分发器集合。
        var attempts = await Task.WhenAll(groups.Select(async group =>
            (group.Id, group.Inputs, Quiet: await QuiesceRecoveryGroupAsync(group.Id, group.Inputs))));
        var restarted = false;
        var blocked = new List<RuntimeQuiescenceResult>();
        foreach (var attempt in attempts)
        {
            if (!attempt.Quiet.Succeeded)
            {
                _waitingRecoveryGroups.Add(attempt.Id);
                blocked.Add(attempt.Quiet);
                continue;
            }
            var outputs = _outputRecoveryGroups.Where(pair => pair.Value == attempt.Id).Select(pair => pair.Key).ToHashSet();
            var retained = _activeOutputs.ToDictionary(output => output.Target.Id);
            await using var prepared = PrepareCore(_activeDefinition, _recoverySenderStates, attempt.Inputs, retained, outputs);
            prepared.HandOver();
            var dispatchers = attempt.Inputs.Select(id => RuntimeUnitId.Create("delivery.dispatcher", id.Key)).ToHashSet();
            bool Replaced(RuntimeUnitId id) => attempt.Inputs.Contains(id) || outputs.Contains(id) || dispatchers.Contains(id);
            lock (_lock)
            {
                _dispatchers.RemoveAll(dispatcher => attempt.Inputs.Contains(dispatcher.InputId));
                foreach (var id in attempt.Inputs)
                { _inputChannels.Remove(id); }
                _notStarted.RemoveAll(unit => Replaced(unit.Id));
                foreach (var input in prepared.Units.Where(unit => attempt.Inputs.Contains(unit.Id)))
                { _activeInputs[input.Id] = input; }
                // 失败输出不接入新分发器；发送状态单独保留供修复后重试，避免重新发送已 ACK 的记录。
                _activeOutputs = prepared.Outputs;
                foreach (var output in prepared.Outputs)
                { _recoverySenderStates[output.Target.ConnectionId] = output.State; }
            }
            _supervisor.ForgetStopped(Replaced);
            try
            {
                await StartPreparedUnitsAsync(prepared, []);
                prepared.CompleteActivation();
            }
            catch
            {
                prepared.CancelActivation();
                throw;
            }
            restarted = true;
            _waitingRecoveryGroups.Remove(attempt.Id);
            if (prepared.Failures.Count == 0)
            {
                _recoveryGroups.Remove(attempt.Id);
                _recoveryFailures.Remove(attempt.Id);
            }
            else
            { _recoveryFailures[attempt.Id] = [.. prepared.Failures]; }
            _events.Record(RuntimeEventSeverity.Info, "runtime.inputsRestored", attempt.Id.ToString(),
                "已按原有效版本恢复本组可用输入；构建失败单元保持故障，其他等待退出的组继续恢复。");
        }
        _quiescenceRequested = _recoveryGroups.Count != 0;
        var pending = blocked.Count == 0 ? null : new RuntimeQuiescenceResult(_activeRevision,
            blocked.All(result => result.Drained), [.. blocked.SelectMany(result => result.TimedOutUnits).Distinct()]);
        return new(_activeRevision, restarted, pending, [.. _recoveryFailures.Values.SelectMany(failures => failures)]);
    }

    private async Task<RuntimeQuiescenceResult> QuiesceRecoveryGroupAsync(RuntimeUnitId group, HashSet<RuntimeUnitId> inputs)
    {
        var timeout = _options.UnitStopTimeout;
        var retiring = await StopRetiringSendersAsync(group);
        if (retiring.Count != 0)
        { return new(_activeRevision, false, [.. retiring]); }
        var stopped = await _supervisor.StopAsync(inputs.Contains, timeout, CancellationToken.None);
        if (stopped.Count != 0)
        { return new(_activeRevision, false, [.. stopped]); }
        var dispatchers = _dispatchers.Where(dispatcher => inputs.Contains(dispatcher.InputId)).ToArray();
        foreach (var dispatcher in dispatchers)
        { dispatcher.Channel.Writer.TryComplete(); }
        try
        { await Task.WhenAll(dispatchers.Select(dispatcher => dispatcher.State.Drained.Task)).WaitAsync(timeout, _time, CancellationToken.None); }
        catch (TimeoutException) { return new(_activeRevision, false, []); }
        var dispatcherIds = inputs.Select(id => RuntimeUnitId.Create("delivery.dispatcher", id.Key)).ToHashSet();
        var stoppedDispatchers = await _supervisor.StopAsync(dispatcherIds.Contains, timeout, CancellationToken.None);
        if (stoppedDispatchers.Count != 0)
        { return new(_activeRevision, true, [.. stoppedDispatchers]); }
        var outputs = _senders.Values.Where(output => output.RecoveryGroup == group).Select(output => output.Target.Id).ToHashSet();
        var stoppedOutputs = await _supervisor.StopAsync(outputs.Contains, timeout, CancellationToken.None);
        return new(_activeRevision, true, [.. stoppedOutputs]);
    }
}

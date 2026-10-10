using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

public sealed partial class RuntimeHost
{
    // 与当前格式定义分开：保留的发送者可以仍使用旧路径 ID，但只发送已冻结的记录。
    private readonly Dictionary<long, PreparedOutput> _senders = [];
    private readonly Dictionary<long, OutputSenderState> _senderStates = [];
    private readonly Dictionary<long, HashSet<RuntimeUnitId>> _retiringSenderGroups = [];
    private readonly Dictionary<long, HashSet<RuntimeUnitId>> _senderConnectionGroups = [];

    /// <summary>应用第 5 步：宿主已提交新 effectiveRevision 后，切换输入及发送者。
    /// 调用方必须持有完整应用/回滚锁，完成静态校验和静默后的目标变更检查（含禁止删除尚有待发数据的目标）。
    /// 此方法不提交配置，也不能证明配置已提交；进入后不接受请求取消、不自动回滚有效版本。
    /// 停止超时的独立发送者保留原实例，替换者等其完整退出后才构建；输入按新版本启动。</summary>
    public Task ActivateCommittedAsync(PreparedPlan prepared, RuntimeQuiescenceResult quiescence) =>
        ActivateCommittedCoreAsync(prepared, quiescence, retryEffective: false);

    /// <summary>宿主已验证并重新标记当前有效快照后人工重试；仍须完整静默并遵守单实例规则。
    /// 允许相同版本重新激活，不生成新版本；不能通过普通应用入口复用版本号。</summary>
    public Task RetryEffectiveAsync(PreparedPlan prepared, RuntimeQuiescenceResult quiescence) =>
        ActivateCommittedCoreAsync(prepared, quiescence, retryEffective: true);

    private async Task ActivateCommittedCoreAsync(PreparedPlan prepared, RuntimeQuiescenceResult quiescence, bool retryEffective)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(quiescence);
        var operation = EnterOperation();
        try
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_currentQuiescence, quiescence) || quiescence.Revision != _activeRevision ||
                    prepared.Revision < _activeRevision || !retryEffective && prepared.Revision == _activeRevision)
                { throw new InvalidOperationException("必须先完成当前有效版本的完整静默，并提交一个递增的新版本。"); }
                if (retryEffective && prepared.Revision == _activeRevision && (_activeDefinition is null ||
                    !prepared.Definition.Units.Select(unit => (unit.Id, unit.ConfigurationHash))
                        .SequenceEqual(_activeDefinition.Units.Select(unit => (unit.Id, unit.ConfigurationHash)))))
                { throw new InvalidOperationException("人工重试不能在相同版本号下替换计划内容。"); }
                // 失效/重复计划在改变任何运行状态之前拒绝。
                prepared.HandOver();
                _currentQuiescence = null;
                _activeRevision = prepared.Revision;
                _activeDefinition = prepared.Definition;
            }

            try
            {
                await CancelRecoveryAsync();
                var blockedGroups = await TransitionSendersAsync(prepared);
                lock (_lock)
                {
                    _activeInputs.Clear();
                    _outputRecoveryGroups.Clear();
                    _dispatchers.Clear();
                    _inputChannels.Clear();
                    _notStarted.Clear();
                    _recoveryGroups.Clear();
                    _waitingRecoveryGroups.Clear();
                    _recoveryFailures.Clear();
                    _recoverySenderStates.Clear();
                    _quiescenceRequested = false;
                    foreach (var input in prepared.Units.Where(unit => unit.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal)))
                    { _activeInputs[input.Id] = input; }
                    foreach (var output in prepared.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal) && unit.RecoveryGroup is not null))
                    { _outputRecoveryGroups[output.Id] = output.RecoveryGroup!; }
                    foreach (var output in _activeOutputs.Where(output => output.RecoveryGroup is not null))
                    { _outputRecoveryGroups[output.Target.Id] = output.RecoveryGroup!; }
                }
                _supervisor.ForgetStopped();
                _recoveryGroups.UnionWith(blockedGroups);
                _waitingRecoveryGroups.UnionWith(blockedGroups);
                _quiescenceRequested = blockedGroups.Count != 0;
                foreach (var output in _activeOutputs)
                { _recoverySenderStates[output.Target.ConnectionId] = output.State; }
                // 发送者已协调；丢弃准备阶段尚未借用任何适配器的占位实例，仅启动输入和分发器。
                var skipped = prepared.Outputs.Select(output => output.Target.Id).ToHashSet();
                skipped.UnionWith(prepared.Units.Where(unit => unit.RecoveryGroup is not null && blockedGroups.Contains(unit.RecoveryGroup)).Select(unit => unit.Id));
                await StartPreparedUnitsAsync(prepared, skipped);
                prepared.CompleteActivation();
                StartRecoveryIfNeeded();
                _events.Record(RuntimeEventSeverity.Info, "runtime.versionActivated", null,
                    $"运行时已接管有效版本 r{prepared.Revision}；等待旧实例退出的单元不计为已经启动，也不自动回滚。");
            }
            catch
            {
                prepared.CancelActivation();
                // 配置已提交。保留新版本和单元故障，不恢复旧输入，也不把旧版本伪报为有效版本。
                throw;
            }
        }
        finally { CompleteOperation(operation); }
    }

    private async Task<HashSet<RuntimeUnitId>> TransitionSendersAsync(PreparedPlan prepared)
    {
        var next = prepared.Outputs.GroupBy(output => output.Target.ConnectionId).ToDictionary(group => group.Key, group => group.First());
        var statuses = _supervisor.Snapshot().ToDictionary(status => status.Id);
        var retained = _senders.Where(pair => next.TryGetValue(pair.Key, out var output) &&
            !pair.Value.Target.QuiesceWithInputs && !output.Target.QuiesceWithInputs && pair.Value.Target.CanShareSenderWith(output.Target) &&
            statuses.TryGetValue(pair.Value.Target.Id, out var status) && status.State is RuntimeUnitState.Running or RuntimeUnitState.Backoff &&
            !prepared.Outputs.Any(candidate => candidate.Target.Id == pair.Value.Target.Id && candidate.Target.ConnectionId != pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var retainedIds = retained.Values.Select(output => output.Target.Id).ToHashSet();
        await _supervisor.StopAsync(id => id.Kind.StartsWith("output.", StringComparison.Ordinal) && !retainedIds.Contains(id),
            _options.UnitStopTimeout, CancellationToken.None);
        var inputGroups = prepared.Units.Where(unit => unit.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal))
            .Select(unit => unit.RecoveryGroup).ToHashSet();
        foreach (var output in next.Values)
        { RememberSenderConnection(output); }
        // 被上一版本移除或尚未启动的路径也可能仍有旧物理实例退出中，不能只检查当前格式定义。
        foreach (var connection in _senderConnectionGroups.Where(pair => !retained.ContainsKey(pair.Key) && _supervisor.IsGroupOccupied(SenderGroup(pair.Key))))
        {
            var groups = connection.Value.Where(inputGroups.Contains).ToHashSet();
            if (groups.Count != 0)
            { _retiringSenderGroups[connection.Key] = groups; }
        }
        var blockedGroups = _retiringSenderGroups.Values.SelectMany(groups => groups).ToHashSet();

        // 只接续状态引用，不在旧发送者可能仍写入 ACK/失败结果时复制或清零。
        foreach (var output in next.Values)
        { _senderStates.TryAdd(output.Target.ConnectionId, output.State); }
        _activeOutputs = prepared.Outputs.Select(output => output with { State = _senderStates[output.Target.ConnectionId] }).ToArray();
        _senders.Clear();
        foreach (var output in _activeOutputs.GroupBy(output => output.Target.ConnectionId).Select(group => group.First()))
        {
            var connection = output.Target.ConnectionId;
            if (output.RecoveryGroup is not null && blockedGroups.Contains(output.RecoveryGroup))
            {
                _senders.Add(connection, output);
                continue;
            }
            if (retained.TryGetValue(connection, out var existing))
            {
                _senders.Add(connection, existing);
                continue;
            }
            var reset = true;
            IRuntimeUnit Build()
            {
                if (reset)
                {
                    // 监督者保证全部前驱已退出。配置应用重新探测，但未提交的持久化结果不能丢失。
                    ResetSenderProbe(output.State);
                    reset = false;
                }
                return new OutputSenderUnit(output.Target, output.State, _deliveries, _time, _events,
                    new RestartBackoff(_options.InitialRestartDelay, _options.MaximumRestartDelay, _options.RestartJitter, _random), _controls);
            }
            if (!_supervisor.TryRestartStopped(output.Target.Id, Build, null, SenderGroup(connection)))
            { throw new InvalidOperationException("发送者交接仍有未撤销的资源占用。"); }
            _senders.Add(connection, output);
        }
        foreach (var connection in _senderStates.Keys.Where(connection => !next.ContainsKey(connection) && !_supervisor.IsGroupOccupied(SenderGroup(connection))).ToArray())
        { _senderStates.Remove(connection); _senderConnectionGroups.Remove(connection); }
        // 不对旧队列作全局 missing-target 隔离；每个发送者按冻结指纹核对自己的连接。
        return blockedGroups;
    }

    private void RememberSenderConnection(PreparedOutput output)
    {
        if (output.ConnectionGroup is not { } group)
        { return; }
        if (!_senderConnectionGroups.TryGetValue(output.Target.ConnectionId, out var groups))
        { _senderConnectionGroups[output.Target.ConnectionId] = groups = []; }
        groups.Add(group);
    }

    private async Task<IReadOnlyList<RuntimeUnitId>> StopRetiringSendersAsync(RuntimeUnitId? group)
    {
        // 分组恢复可并行调用；这里只在锁内取快照/删条目，不跨 await 持锁。
        long[] retiring;
        lock (_lock)
        { retiring = _retiringSenderGroups.Where(pair => group is null || pair.Value.Contains(group)).Select(pair => pair.Key).ToArray(); }
        var results = await Task.WhenAll(retiring.Select(async connection =>
            (Key: connection, Stopped: await _supervisor.StopGroupAsync(SenderGroup(connection), _options.UnitStopTimeout))));
        lock (_lock)
        {
            foreach (var result in results.Where(result => result.Stopped.Count == 0))
            {
                _retiringSenderGroups.Remove(result.Key);
                if (_senderStates.TryGetValue(result.Key, out var state))
                { ResetSenderProbe(state); }
            }
        }
        return results.SelectMany(result => result.Stopped).ToArray();
    }

    private static void ResetSenderProbe(OutputSenderState state)
    {
        state.SuspendedVersion = null;
        state.RetryDelay = null;
        state.ConsecutiveRejects = 0;
        state.ConsecutiveRetries = 0;
        state.ReconciledFingerprint = null;
        state.RecheckHeadSeq = null;
    }

    private static RuntimeUnitId SenderGroup(long connection) => RuntimeUnitId.Create("sender.connection",
        connection.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

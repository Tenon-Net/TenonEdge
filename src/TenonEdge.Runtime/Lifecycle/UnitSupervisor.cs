using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 运行单元的监督者（总设计 §8.1）：按单元标识登记被监督的单元，执行单实例规则（总设计 §7.4）——
/// 同一标识的上一个实例的循环尚未退出时，不启动新实例，包括上一个实例停止超时的情况。
/// </summary>
internal sealed class UnitSupervisor
{
    private readonly Dictionary<RuntimeUnitId, SupervisedUnit> _units = [];
    private readonly Dictionary<RuntimeUnitId, PendingRestart> _pending = [];
    private readonly Dictionary<RuntimeUnitId, RuntimeUnitId> _exclusiveGroups = [];
    private readonly Lock _lock = new();
    private readonly RestartBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly IRuntimeEventSink _events;
    private readonly ILogger _logger;

    public UnitSupervisor(RuntimeOptions options, TimeProvider time, IRuntimeEventSink events, ILogger logger, Func<double>? random = null)
    {
        _backoff = new RestartBackoff(options.InitialRestartDelay, options.MaximumRestartDelay, options.RestartJitter, random ?? Random.Shared.NextDouble);
        _time = time;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// 登记并启动一个单元。同一标识的上一个实例仍存活时不启动，返回 false，调用方负责释放传入的实例。
    /// </summary>
    /// <param name="rebuild">故障后用同一计划与上下文构建新实例。</param>
    /// <param name="exclusiveGroup">可选的互斥资源标识；输出使用 connectionId，不因路线路径改名而释放占用。</param>
    public bool TryStart(IRuntimeUnit instance, Func<IRuntimeUnit> rebuild, InputReadStatus? reads, RuntimeUnitId? exclusiveGroup = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(rebuild);
        lock (_lock)
        {
            if (HasPendingConflict(instance.Id, exclusiveGroup) || ConflictingUnits(instance.Id, exclusiveGroup).Any(unit => unit.IsAlive))
            {
                return false;
            }

            var unit = new SupervisedUnit(instance, rebuild, _backoff, _time, _events, _logger, reads);
            _units[instance.Id] = unit;
            SetExclusiveGroup(instance.Id, exclusiveGroup);
            unit.Start();
            return true;
        }
    }

    /// <summary>为已请求停止的单元登记当前有效计划的重建工厂。旧循环、取消回调及释放全部结束后才构建。
    /// 返回 true 表示已登记，不表示已启动；等待期间重复登记只更新为最新工厂。仍正常运行的单元不接受替换。
    /// 同时等待同 ID 及同互斥资源的全部旧实例；不同 ID 的待启动登记占用同一资源时拒绝，调用方须先撤销旧登记。
    /// StopAsync 按待启动单元的 ID 撤销登记，停机后不会由本登记复活单元。</summary>
    public bool TryRestartStopped(RuntimeUnitId id, Func<IRuntimeUnit> rebuild, InputReadStatus? reads, RuntimeUnitId? exclusiveGroup = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(rebuild);
        lock (_lock)
        {
            if (_pending.TryGetValue(id, out var pending))
            {
                // 改变互斥资源必须先撤销旧登记，再按新的全部前驱登记，不能沿用旧等待任务。
                if (pending.ExclusiveGroup != exclusiveGroup)
                { return false; }
                pending.Rebuild = rebuild;
                pending.Reads = reads;
                return true;
            }
            if (HasPendingConflict(id, exclusiveGroup))
            { return false; }
            var previous = ConflictingUnits(id, exclusiveGroup).ToArray();
            if (previous.Any(unit => unit.Snapshot().State is not
                (RuntimeUnitState.Stopped or RuntimeUnitState.Stopping or RuntimeUnitState.StopTimedOut)))
            { return false; }

            if (previous.All(unit => unit.Completion.IsCompleted))
            {
                StartDeferred(id, rebuild, reads, exclusiveGroup);
                return true;
            }

            pending = new PendingRestart(rebuild, reads, exclusiveGroup);
            _pending.Add(id, pending);
            pending.Completion = Task.Run(() => RestartAfterExitAsync(id, Task.WhenAll(previous.Select(unit => unit.Completion)), pending));
            return true;
        }
    }

    // 调用方持有监督者锁；此处只登记，适配器工厂在 SupervisedUnit 的受监督循环中运行。
    private void StartDeferred(RuntimeUnitId id, Func<IRuntimeUnit> rebuild, InputReadStatus? reads, RuntimeUnitId? exclusiveGroup)
    {
        var unit = new SupervisedUnit(id, rebuild, _backoff, _time, _events, _logger, reads);
        _units[id] = unit;
        SetExclusiveGroup(id, exclusiveGroup);
        unit.Start();
    }

    // 单元 ID 与物理资源分别互斥：输出路径可改名，同一路径也可改用另一个连接。
    private IEnumerable<SupervisedUnit> ConflictingUnits(RuntimeUnitId id, RuntimeUnitId? group) =>
        _units.Where(pair => pair.Key == id || (group is not null && _exclusiveGroups.GetValueOrDefault(pair.Key) == group))
            .Select(pair => pair.Value);

    private bool HasPendingConflict(RuntimeUnitId id, RuntimeUnitId? group) =>
        _pending.Any(pair => pair.Key == id || (group is not null && pair.Value.ExclusiveGroup == group));

    private void SetExclusiveGroup(RuntimeUnitId id, RuntimeUnitId? group)
    {
        if (group is null)
        { _exclusiveGroups.Remove(id); }
        else
        { _exclusiveGroups[id] = group; }
    }

    private async Task RestartAfterExitAsync(RuntimeUnitId id, Task previous, PendingRestart pending)
    {
        try
        {
            try
            { await previous.WaitAsync(pending.Cancel.Token); }
            catch (OperationCanceledException) { return; }
            lock (_lock)
            {
                if (!_pending.TryGetValue(id, out var current) || !ReferenceEquals(current, pending))
                { return; }
                _pending.Remove(id);
                StartDeferred(id, pending.Rebuild, pending.Reads, pending.ExclusiveGroup);
            }
        }
        finally
        {
            // 与 StopAsync 的取消串行；正常重启和撤销都释放等待令牌。
            lock (_lock)
            {
                if (_pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending))
                { _pending.Remove(id); }
                pending.Dispose();
            }
        }
    }

    private sealed class PendingRestart(Func<IRuntimeUnit> rebuild, InputReadStatus? reads, RuntimeUnitId? exclusiveGroup) : IDisposable
    {
        public RuntimeUnitId? ExclusiveGroup { get; } = exclusiveGroup;
        public Func<IRuntimeUnit> Rebuild { get; set; } = rebuild;
        public InputReadStatus? Reads { get; set; } = reads;
        public CancellationTokenSource Cancel { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public void Dispose() => Cancel.Dispose();
    }

    /// <summary>该标识是否有循环尚未退出的实例。</summary>
    public bool IsAlive(RuntimeUnitId id)
    {
        lock (_lock)
        {
            return _units.TryGetValue(id, out var unit) && unit.IsAlive;
        }
    }

    /// <summary>并行停止满足条件的单元并等待它们退出，返回在期限内没有退出的单元。</summary>
    public async Task<IReadOnlyList<RuntimeUnitId>> StopAsync(Func<RuntimeUnitId, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SupervisedUnit[] targets;
        PendingRestart[] pending;
        lock (_lock)
        {
            pending = [.. _pending.Where(pair => predicate(pair.Key)).Select(pair => pair.Value)];
            foreach (var id in _pending.Keys.Where(predicate).ToArray())
            { _pending.Remove(id); }
            foreach (var restart in pending)
            { restart.Cancel.Cancel(); }
            targets = [.. _units.Values.Where(unit => predicate(unit.Id))];
        }

        await Task.WhenAll(pending.Select(restart => restart.Completion));
        var results = await Task.WhenAll(targets.Select(async unit => (unit.Id, Stopped: await unit.StopAsync(timeout, cancellationToken))));
        return [.. results.Where(result => !result.Stopped).Select(result => result.Id)];
    }

    /// <summary>丢弃已完整退出且没有待启动登记的旧状态；切换时不能隐藏活跃或停止超时的实例。</summary>
    public void ForgetStopped(Func<RuntimeUnitId, bool>? predicate = null)
    {
        lock (_lock)
        {
            foreach (var id in _units.Where(pair => !pair.Value.IsAlive && !_pending.ContainsKey(pair.Key) && (predicate is null || predicate(pair.Key))).Select(pair => pair.Key).ToArray())
            { _units.Remove(id); _exclusiveGroups.Remove(id); }
        }
    }

    public IReadOnlyList<RuntimeUnitStatus> Snapshot()
    {
        lock (_lock)
        {
            return [.. _units.Values.Select(unit => unit.Snapshot())];
        }
    }
}

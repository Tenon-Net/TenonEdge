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
    public bool TryStart(IRuntimeUnit instance, Func<IRuntimeUnit> rebuild, InputReadStatus? reads)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(rebuild);
        lock (_lock)
        {
            if (_units.TryGetValue(instance.Id, out var existing) && existing.IsAlive)
            {
                return false;
            }

            var unit = new SupervisedUnit(instance, rebuild, _backoff, _time, _events, _logger, reads);
            _units[instance.Id] = unit;
            unit.Start();
            return true;
        }
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
        lock (_lock)
        {
            targets = [.. _units.Values.Where(unit => predicate(unit.Id))];
        }

        var results = await Task.WhenAll(targets.Select(async unit => (unit.Id, Stopped: await unit.StopAsync(timeout, cancellationToken))));
        return [.. results.Where(result => !result.Stopped).Select(result => result.Id)];
    }

    public IReadOnlyList<RuntimeUnitStatus> Snapshot()
    {
        lock (_lock)
        {
            return [.. _units.Values.Select(unit => unit.Snapshot())];
        }
    }
}

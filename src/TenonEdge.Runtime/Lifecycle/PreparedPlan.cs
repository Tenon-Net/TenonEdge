using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 已按运行计划构建、尚未启动的运行单元（总设计 §7.4 第 2 步“准备”）。构建只做本地计算，此时没有任何输入在采集。
/// <list type="bullet">
/// <item><see cref="Failures"/> 列出构建失败的单元及原因（结构性问题）。应用新版本时有任何失败即放弃该版本；
/// 启动时加载有效版本，只停止失败的单元，其余照常激活（总设计 §7.4）。</item>
/// <item>没有激活就释放时，释放已构建的实例；激活之后实例归监督者所有。</item>
/// </list>
/// </summary>
public sealed class PreparedPlan : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private bool _handedOver;
    private readonly TaskCompletionSource _activation;

    internal PreparedPlan(long revision, IReadOnlyList<PreparedUnit> units, IReadOnlyList<PreparedOutput> outputs, TaskCompletionSource activation)
    {
        _activation = activation;
        Revision = revision;
        Units = units;
        Outputs = outputs;
        Failures = [.. units.Where(unit => unit.Error is not null).Select(unit => new RuntimeUnitBuildFailure(unit.Id, unit.Error!))];
    }

    public long Revision { get; }

    public IReadOnlyList<RuntimeUnitBuildFailure> Failures { get; }

    internal IReadOnlyList<PreparedUnit> Units { get; }

    internal IReadOnlyList<PreparedOutput> Outputs { get; }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_handedOver)
            {
                return;
            }

            _handedOver = true;
        }

        _activation.TrySetCanceled();
        foreach (var unit in Units)
        {
            if (unit.Instance is not null)
            {
                await unit.Instance.DisposeAsync();
            }
        }
    }

    internal void CompleteActivation() => _activation.TrySetResult();
    internal void CancelActivation() => _activation.TrySetCanceled();

    /// <summary>把已构建的实例交给监督者；每个计划只能交出一次，交出之后释放计划不再释放实例。</summary>
    internal void HandOver()
    {
        lock (_lock)
        {
            if (_handedOver)
            {
                throw new InvalidOperationException($"版本 r{Revision} 的运行单元已经激活或已释放。");
            }

            _handedOver = true;
        }
    }
}

/// <summary>构建失败的运行单元及原因。</summary>
public sealed record RuntimeUnitBuildFailure(RuntimeUnitId Id, string Reason);

/// <summary>一个已构建（或构建失败）的输入单元，连同它的通道、读取状态与重新构建的方法。</summary>
internal sealed record PreparedUnit(
    RuntimeUnitId Id,
    IRuntimeUnit? Instance,
    string? Error,
    Func<IRuntimeUnit>? Rebuild,
    Channel<EdgeMessage>? Channel,
    InputReadStatus? Reads);

internal sealed record PreparedOutput(IOutputTarget Target, TenonEdge.Runtime.Mapping.FieldMappingPipeline? Mapping);

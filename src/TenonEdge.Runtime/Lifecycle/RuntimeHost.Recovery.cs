using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>旧有效版本的输入恢复结果。未退出实例仍然阻止重建；构建失败按单元显示，不代表新版本已生效。</summary>
public sealed record RuntimeInputRecoveryResult(long? Revision, bool Restarted,
    RuntimeQuiescenceResult? BlockedBy, ImmutableArray<RuntimeUnitBuildFailure> Failures)
{
    public bool Succeeded => BlockedBy is null && Failures.IsEmpty;
}

public sealed partial class RuntimeHost
{
    /// <summary>提交之前放弃应用后，按原有效计划构建新的输入通道/协议上下文，恢复输入及耦合发送者。
    /// 不改有效版本，不改队列记录；独立发送者及其待提交确认状态保留。
    /// 未完成的旧排空会再次尝试，仍有旧实例或在途消息时返回 BlockedBy，调用方须继续安排恢复，不能据此提交新版本。
    /// 不接受请求取消：这是运行时拥有的恢复操作，停机会等本次有界排空尝试退出。</summary>
    public async Task<RuntimeInputRecoveryResult> RestoreInputsAsync()
    {
        var operation = EnterOperation();
        try
        {
            if (!_quiescenceRequested || _activeDefinition is null)
            { return new(_activeRevision, false, null, []); }
            var quiet = await QuiesceCoreAsync();
            if (!quiet.Succeeded)
            { return new(_activeRevision, false, quiet, []); }

            var retained = _activeOutputs.Where(output => !output.Target.QuiesceWithInputs && _supervisor.IsAlive(output.Target.Id))
                .Select(output => output.Target.Id).ToHashSet();
            // 已确认但尚未删库等发送状态跨新会话保留，恢复不能把 ACK 过的旧记录再发一遍。
            var states = _activeOutputs.GroupBy(output => output.Target.ConnectionId).ToDictionary(group => group.Key, group => group.First().State);
            await using var prepared = PrepareCore(_activeDefinition, states);
            prepared.HandOver();
            lock (_lock)
            {
                _dispatchers.Clear();
                _inputChannels.Clear();
                _notStarted.Clear();
                _activeOutputs = [.. prepared.Outputs, .. _activeOutputs.Where(output => retained.Contains(output.Target.Id) &&
                    prepared.Outputs.All(next => next.Target.Id != output.Target.Id))];
            }
            _supervisor.ForgetStopped();
            try
            {
                await StartPreparedUnitsAsync(prepared, retained);
                prepared.CompleteActivation();
                _quiescenceRequested = prepared.Failures.Count != 0;
            }
            catch
            {
                prepared.CancelActivation();
                throw;
            }
            _events.Record(RuntimeEventSeverity.Info, "runtime.inputsRestored", null,
                "已按原有效版本重新启动可用输入；构建失败的单元保持故障，不改变有效版本。");
            return new(_activeRevision, true, null, [.. prepared.Failures]);
        }
        finally { CompleteOperation(operation); }
    }
}

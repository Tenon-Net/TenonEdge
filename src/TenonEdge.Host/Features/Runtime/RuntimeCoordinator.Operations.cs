using System.Runtime.ExceptionServices;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    private readonly RuntimeApplicationHistory _applications = new(time);

    /// <summary>最近一次操作（可能已结束）；只含脱敏进度，不含请求、异常对象或草稿内容。</summary>
    public RuntimeApplicationStatus? ApplicationStatus
    {
        get { lock (_lock) { return _applications.Latest; } }
    }

    public RuntimeApplicationStatus? ReadApplication(Guid id)
    {
        lock (_lock)
        { return _applications.Read(id); }
    }

    public RuntimeApplicationStatus[] ReadApplications()
    {
        lock (_lock)
        { return _applications.Snapshot(); }
    }

    // 拥有的任务把异常转换为内部结果，不因请求取消而留下无人观察的故障任务。
    // 最近操作列表只保留脱敏状态，原异常只交给仍在等待的调用方。
    private async Task<ApplicationOutcome> RunOperationAsync(Guid id, Func<Task<RuntimeApplicationResult>> execute, TaskCompletionSource completion)
    {
        try
        {
            var result = await execute();
            lock (_lock)
            {
                _applications.Complete(id, result);
            }
            return new(result, null);
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                _applications.Fail(id, exception);
            }
            return new(null, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            lock (_lock)
            {
                _application = null;
                completion.TrySetResult();
            }
        }
    }

    private static async Task<RuntimeApplicationResult> AwaitApplicationAsync(Task<ApplicationOutcome> owned, CancellationToken cancellationToken)
    {
        var outcome = await owned.WaitAsync(cancellationToken);
        outcome.Error?.Throw();
        return outcome.Result!;
    }

    private void AdvanceApplication(Guid id, RuntimeApplicationStage stage, long revision, bool? committed = false)
    {
        lock (_lock)
        {
            _applications.Advance(id, stage, revision, committed);
        }
    }

    private sealed record ApplicationOutcome(RuntimeApplicationResult? Result, ExceptionDispatchInfo? Error);
}

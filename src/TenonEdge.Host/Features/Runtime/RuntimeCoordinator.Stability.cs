namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    private CancellationTokenSource? _stabilityCancellation;
    private Task? _stabilityDelay;
    private TaskCompletionSource _versionChanged = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 调用方持有协调者锁；在公布激活完成之前创建单调计时器，不能在后台稍后按过时的剩余时长注册。
    private void ResetStabilityClock()
    {
        CancelStabilityTimer();
        if (!_activeIsStable)
        {
            _stabilityCancellation = new CancellationTokenSource();
            _stabilityDelay = Task.Delay(settings.StableAfter, time, _stabilityCancellation.Token);
        }
        SignalVersionChange();
    }

    private void CancelStabilityTimer()
    {
        _stabilityDelay = null;
        _stabilityCancellation?.Cancel();
        _stabilityCancellation?.Dispose();
        _stabilityCancellation = null;
    }

    private void SignalVersionChange()
    {
        var previous = _versionChanged;
        _versionChanged = NewSignal();
        previous.TrySetResult();
    }

    /// <summary>同一个后台服务持续观察当前版本，不在首个稳定期之后退出，也不为每个版本增加 HostedService。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Task changed;
                Task? delay;
                long? revision;
                lock (_lock)
                {
                    changed = _versionChanged.Task;
                    revision = _activeRevision;
                    delay = _state == RuntimeCoordinatorState.Active && revision is not null && !_activeIsStable ? _stabilityDelay : null;
                }
                if (delay is null)
                {
                    await changed.WaitAsync(stoppingToken);
                    continue;
                }
                var completed = await Task.WhenAny(delay, changed).WaitAsync(stoppingToken);
                if (completed == changed)
                { continue; }

                Task? application = null;
                lock (_lock)
                {
                    if (_stopping is not null)
                    { return; }
                    if (_application is not null)
                    { application = _application.Task; }
                    else if (_activeRevision == revision && ReferenceEquals(delay, _stabilityDelay) && delay.IsCompletedSuccessfully &&
                        !_activeIsStable && _state == RuntimeCoordinatorState.Active)
                    {
                        try
                        {
                            // 与应用操作的领取串行；不让旧版本的定时器在新版本提交期间写入稳定标记。
                            if (versions.MarkStable(revision!.Value))
                            {
                                LogStable(logger, revision.Value, settings.StableAfter);
                                runtimeEvents.Record(RuntimeEventLevel.Info, RuntimeEventTypes.VersionStable, Target(revision.Value),
                                    $"版本 r{revision.Value} 已连续运行 {settings.StableAfter.TotalSeconds:0} 秒，记为已稳定。");
                            }
                            _activeIsStable = true;
                            CancelStabilityTimer();
                        }
                        catch (Exception exception) when (IsStorageError(exception))
                        {
                            LogStableNotRecorded(logger, revision!.Value);
                            ResetStabilityClock(); // 存储修复后再完整等待一期，保守重试，不忙循环。
                        }
                    }
                }
                if (application is not null)
                { await application.WaitAsync(stoppingToken); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        lock (_lock)
        { CancelStabilityTimer(); }
        base.Dispose();
    }
}

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 监督一个运行单元（总设计 §8.1）：在线程池线程上运行单元的循环，异常在单元边界捕获，记录最后错误，
/// 释放该实例后按退避构建新实例再运行；请求停止时取消令牌并等待循环退出。
/// <list type="bullet">
/// <item>单实例：只有上一个实例的 <see cref="IRuntimeUnit.RunAsync"/> 返回并释放之后，才构建下一个实例，同一时刻最多一个实例存活。</item>
/// <item>停止超时：期限内循环没有退出时，状态为“停止超时”，<see cref="IsAlive"/> 仍为 true，直到循环真正退出。</item>
/// <item>停止时不内联执行取消回调（例如单元释放连接）：回调在线程池上执行，监督循环等回调执行完，才释放实例并结束。
/// 回调抛出的异常在循环结束之前记为停止故障（日志与运行事件），不向调用方抛出；回调阻塞时循环不结束，
/// 由停止期限判定“停止超时”，单元仍占用其标识。停止时正在运行的实例要等回调执行完才释放。</item>
/// <item>状态：“停止中”只从运行中或退避转入；“已停止”只由监督循环在结束时设置，停止不会把它改成别的状态；
/// 再次停止时保持“停止超时”，超时事件只在第一次转入时记录。</item>
/// <item>故障后先启动退避计时，再公布故障与下次重启时间。</item>
/// <item>监督循环本身不抛出异常，不让单元的异常冒泡到宿主。</item>
/// </list>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "停止用的取消源没有计时器、没有关联令牌，也不访问 WaitHandle，不持有需要释放的资源；单元停止之后状态查询仍会用到它。")]
internal sealed partial class SupervisedUnit
{
    private const int MaximumErrorLength = 500;

    private readonly Func<IRuntimeUnit> _rebuild;
    private readonly RestartBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly IRuntimeEventSink _events;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();

    private IRuntimeUnit? _initial;
    private Task? _loop;
    private Task? _cancellation;
    private RuntimeUnitState _state = RuntimeUnitState.Stopped;
    private int _restartCount;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;
    private DateTimeOffset? _nextRestartAt;

    public SupervisedUnit(
        IRuntimeUnit initial,
        Func<IRuntimeUnit> rebuild,
        RestartBackoff backoff,
        TimeProvider time,
        IRuntimeEventSink events,
        ILogger logger,
        InputReadStatus? reads)
    {
        Id = initial.Id;
        _initial = initial;
        _rebuild = rebuild;
        _backoff = backoff;
        _time = time;
        _events = events;
        _logger = logger;
        Reads = reads;
    }

    public RuntimeUnitId Id { get; }

    public InputReadStatus? Reads { get; }

    /// <summary>循环已经开始且尚未退出：此时不得为同一标识创建新实例。</summary>
    public bool IsAlive
    {
        get
        {
            lock (_lock)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null)
            {
                throw new InvalidOperationException($"运行单元 {Id} 已经启动过。");
            }

            _state = RuntimeUnitState.Running;
            _loop = Task.Run(SuperviseAsync);
        }
    }

    /// <summary>
    /// 请求停止并等待循环退出。期限内退出返回 true；到期（或 <paramref name="cancellationToken"/> 先取消）返回 false，
    /// 状态为“停止超时”，单元仍视为存活。可以重复调用：不会再次发出取消，超时事件也不重复记录。
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task loop;
        IRuntimeUnit? neverStarted = null;
        lock (_lock)
        {
            if (_loop is null)
            {
                _state = RuntimeUnitState.Stopped;
                (neverStarted, _initial) = (_initial, null);
            }
        }

        if (neverStarted is not null)
        {
            await DisposeQuietlyAsync(neverStarted);
            return true;
        }

        lock (_lock)
        {
            if (_loop is null)
            {
                return true;
            }

            loop = _loop;
            if (_state is RuntimeUnitState.Running or RuntimeUnitState.Backoff)
            {
                _state = RuntimeUnitState.Stopping;
            }

            // 在锁内发出取消并记下回调的执行，监督循环结束前在锁内读取它，因此一定等得到回调。
            // CancelAsync 不内联执行回调（回调在线程池上执行），锁内不会运行单元的代码。
            _cancellation ??= _stopping.CancelAsync();
        }

        try
        {
            await loop.WaitAsync(timeout, _time, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            bool firstTimeout;
            lock (_lock)
            {
                // 按循环自己设的状态判断，不按任务是否完成：循环在锁内设为“已停止”之后任务才完成，期限恰在其间到期时，单元其实已经停止。
                if (_state == RuntimeUnitState.Stopped)
                {
                    return true;
                }

                firstTimeout = _state != RuntimeUnitState.StopTimedOut;
                _state = RuntimeUnitState.StopTimedOut;
            }

            if (firstTimeout)
            {
                LogStopTimedOut(_logger, Id.ToString(), timeout);
                _events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.UnitStopTimedOut, Id.ToString(), "运行单元停止超时，需重启服务。");
            }

            return false;
        }
    }

    public RuntimeUnitStatus Snapshot()
    {
        lock (_lock)
        {
            return new RuntimeUnitStatus(Id, _state, _restartCount, _lastError, _lastErrorAt, _nextRestartAt, Reads?.Snapshot());
        }
    }

    private async Task SuperviseAsync()
    {
        var stoppingToken = _stopping.Token;
        var consecutiveFailures = 0;
        var callbacksObserved = false;
        var instance = _initial;
        _initial = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            Exception? failure = null;
            var startedAt = _time.GetTimestamp();
            if (instance is null)
            {
                try
                {
                    instance = _rebuild();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failure = new RuntimeUnitBuildException(Id, $"重新构建失败：{exception.Message}", exception);
                }
            }

            if (instance is not null)
            {
                SetRunning();
                failure = await RunOnceAsync(instance, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    // 停止时先等取消回调执行完，再释放实例：回调（例如关闭连接）与释放不会同时操作同一资源。
                    await WaitForCancellationCallbacksAsync();
                    callbacksObserved = true;
                }

                await DisposeQuietlyAsync(instance);
                instance = null;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (_time.GetElapsedTime(startedAt) >= _backoff.ResetAfter)
            {
                consecutiveFailures = 0;
            }

            consecutiveFailures++;
            var delay = _backoff.Delay(consecutiveFailures);

            // 先启动退避计时，再公布故障与下次重启时间：状态显示“退避”时计时已经开始，重启也不因记录事件的耗时而推迟。
            var backoff = Task.Delay(delay, _time, stoppingToken);
            RecordFailure(failure ?? new InvalidOperationException("运行单元的循环意外结束。"), delay, consecutiveFailures);
            try
            {
                await backoff;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 循环只在请求停止之后结束，此时取消已经发出。等取消回调执行完（出错时记下）再结束：回调阻塞时单元仍占用其标识。
        if (!callbacksObserved)
        {
            await WaitForCancellationCallbacksAsync();
        }

        if (instance is not null)
        {
            await DisposeQuietlyAsync(instance);
        }

        lock (_lock)
        {
            _state = RuntimeUnitState.Stopped;
            _nextRestartAt = null;
        }
    }

    /// <summary>运行一次实例的循环。返回 null 表示因请求停止而正常结束；其他情况返回故障原因。</summary>
    private static async Task<Exception?> RunOnceAsync(IRuntimeUnit instance, CancellationToken stoppingToken)
    {
        try
        {
            await instance.RunAsync(stoppingToken);
            return stoppingToken.IsCancellationRequested ? null : new InvalidOperationException("运行单元的循环在没有请求停止时结束。");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return exception;
        }
    }

    private void SetRunning()
    {
        lock (_lock)
        {
            if (_state is not (RuntimeUnitState.Stopping or RuntimeUnitState.StopTimedOut))
            {
                _state = RuntimeUnitState.Running;
            }

            _nextRestartAt = null;
        }
    }

    private void RecordFailure(Exception failure, TimeSpan delay, int consecutiveFailures)
    {
        var now = _time.GetUtcNow();
        var message = failure.Message.Length > MaximumErrorLength ? failure.Message[..MaximumErrorLength] : failure.Message;
        lock (_lock)
        {
            _restartCount++;
            _lastError = message;
            _lastErrorAt = now;
            _nextRestartAt = now + delay;
            if (_state is not (RuntimeUnitState.Stopping or RuntimeUnitState.StopTimedOut))
            {
                _state = RuntimeUnitState.Backoff;
            }
        }

        LogUnitFaulted(_logger, failure, Id.ToString(), consecutiveFailures, delay);
        _events.Record(
            RuntimeEventSeverity.Warning,
            RuntimeEventTypes.UnitFaulted,
            Id.ToString(),
            $"运行单元故障：{message}；{delay.TotalSeconds:0.###} 秒后重启（连续第 {consecutiveFailures} 次）。");
    }

    /// <summary>
    /// 等停止时发出的取消回调执行完。只在请求停止之后调用：取消与 <see cref="_cancellation"/> 在同一锁内记下，这里一定读得到。
    /// 每次停止只调用一次，回调出错只记一次。
    /// </summary>
    private Task WaitForCancellationCallbacksAsync()
    {
        Task? cancellation;
        lock (_lock)
        {
            cancellation = _cancellation;
        }

        return cancellation is null ? Task.CompletedTask : ObserveCancellationAsync(cancellation);
    }

    /// <summary>等待取消回调执行完；回调抛出的异常记为停止故障，不向外抛出。</summary>
    private async Task ObserveCancellationAsync(Task cancelling)
    {
        try
        {
            await cancelling;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var error = exception is AggregateException { InnerExceptions.Count: > 0 } aggregate ? aggregate.InnerExceptions[0] : exception;
            var message = error.Message.Length > MaximumErrorLength ? error.Message[..MaximumErrorLength] : error.Message;
            lock (_lock)
            {
                _lastError = $"停止时出错：{message}";
                _lastErrorAt = _time.GetUtcNow();
            }

            LogStopCallbackFailed(_logger, exception, Id.ToString());
            _events.Record(RuntimeEventSeverity.Warning, RuntimeEventTypes.UnitStopFailed, Id.ToString(), $"运行单元停止时出错：{message}");
        }
    }

    private async Task DisposeQuietlyAsync(IRuntimeUnit instance)
    {
        try
        {
            await instance.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogDisposeFailed(_logger, exception, Id.ToString());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "运行单元 {UnitId} 故障（连续第 {ConsecutiveFailures} 次），{Delay} 后重启。")]
    private static partial void LogUnitFaulted(ILogger logger, Exception exception, string unitId, int consecutiveFailures, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "运行单元 {UnitId} 在 {Timeout} 内没有停止，仍视为存活，需重启服务。")]
    private static partial void LogStopTimedOut(ILogger logger, string unitId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "运行单元 {UnitId} 停止时，取消回调出错。")]
    private static partial void LogStopCallbackFailed(ILogger logger, Exception exception, string unitId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "释放运行单元 {UnitId} 时出错。")]
    private static partial void LogDisposeFailed(ILogger logger, Exception exception, string unitId);
}

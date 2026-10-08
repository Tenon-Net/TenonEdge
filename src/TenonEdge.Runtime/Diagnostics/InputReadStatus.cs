using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 一个数据来源的读取状态（总设计 §6.1、§8.1，功能需求 §7.2），由输入单元经 <see cref="IInputReadReporter"/> 上报：
/// <list type="bullet">
/// <item>最近一次读取全部成功为“正常”，有字段失败为“部分失败”；整组读取失败累计连续次数，连续 <see cref="OfflineAfterConsecutiveFailures"/> 次判为“离线”。</item>
/// <item>判为离线与恢复时各记一条运行事件（功能需求 §7.4），对象为 deviceKey；中间的每次失败只更新状态，不刷屏。</item>
/// <item>瞬时健康状态重启后从“尚未读取”开始；运行时装配的累计计数经队列唯一写者持久化。</item>
/// </list>
/// 单元故障重启后沿用同一个状态对象。
/// </summary>
public sealed class InputReadStatus : IInputReadReporter
{
    /// <summary>设备离线判定（轮询类）：连续 3 个周期整体读取失败（总设计 §8.4）。</summary>
    public const int OfflineAfterConsecutiveFailures = 3;

    private const int MaximumErrorLength = 500;

    private readonly Lock _lock = new();
    private readonly IRuntimeEventSink _events;
    private readonly PendingCounters? _counters;
    private readonly string _counterPrefix;

    private DateTimeOffset? _lastSuccessAt;
    private DateTimeOffset? _lastFailureAt;
    private DateTimeOffset? _lastSkippedAt;
    private string? _lastError;
    private int _lastFailedFieldCount;
    private int _consecutiveFailures;
    private bool _offline;
    private bool _lastReadFailed;
    private long _readsCompleted;
    private long _readsFailed;
    private long _dataRejected;
    private long _cyclesSkipped;
    private long _invalidUpstreamTimes;

    public InputReadStatus(string deviceKey, string inputKey, IRuntimeEventSink events)
        : this(deviceKey, inputKey, events, null) { }

    internal InputReadStatus(string deviceKey, string inputKey, IRuntimeEventSink events, PendingCounters? counters)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceKey);
        ArgumentException.ThrowIfNullOrEmpty(inputKey);
        ArgumentNullException.ThrowIfNull(events);
        DeviceKey = deviceKey;
        InputKey = inputKey;
        _events = events;
        _counters = counters;
        _counterPrefix = $"input/{deviceKey}/{inputKey}/";
    }

    public string DeviceKey { get; }

    public string InputKey { get; }

    public void ReadCompleted(DateTimeOffset completedAt, int failedFieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failedFieldCount);
        bool recovered;
        lock (_lock)
        {
            recovered = _offline;
            _offline = false;
            _consecutiveFailures = 0;
            _lastReadFailed = false;
            _lastSuccessAt = completedAt;
            _lastFailedFieldCount = failedFieldCount;
            _readsCompleted++;
            _counters?.RecordActivity(RuntimeActivity.Received);
            _counters?.Add(_counterPrefix + "readsCompleted");
            _counters?.Add(_counterPrefix + "failedFields", failedFieldCount);
        }

        if (recovered)
        {
            _events.Record(RuntimeEventSeverity.Info, RuntimeEventTypes.DeviceOnline, DeviceKey, $"设备恢复读取（数据来源 {InputKey}）。");
        }
    }

    public void ReadFailed(DateTimeOffset completedAt, string reason, InputReadFailureKind kind = InputReadFailureKind.ReadError)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (!Enum.IsDefined(kind))
        { throw new ArgumentOutOfRangeException(nameof(kind)); }
        var error = reason.Length > MaximumErrorLength ? reason[..MaximumErrorLength] : reason;
        bool wentOffline;
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastReadFailed = true;
            _lastFailureAt = completedAt;
            _lastError = error;
            _readsFailed++;
            if (kind == InputReadFailureKind.DataError)
            {
                _dataRejected++;
                _counters?.Add(_counterPrefix + "dataRejected");
            }
            else
            {
                _counters?.Add(_counterPrefix + "readErrors");
            }
            _counters?.RecordActivity(RuntimeActivity.ReadFailure);
            _counters?.Add(_counterPrefix + "readsFailed");
            wentOffline = !_offline && _consecutiveFailures >= OfflineAfterConsecutiveFailures;
            _offline |= wentOffline;
        }

        if (wentOffline)
        {
            _events.Record(
                RuntimeEventSeverity.Warning,
                RuntimeEventTypes.DeviceOffline,
                DeviceKey,
                $"设备离线：数据来源 {InputKey} 连续 {OfflineAfterConsecutiveFailures} 个周期读取失败，最近一次：{error}");
        }
    }

    public void CycleSkipped(DateTimeOffset skippedAt)
    {
        lock (_lock)
        {
            _cyclesSkipped++;
            _counters?.Add(_counterPrefix + "overruns");
            _lastSkippedAt = skippedAt;
        }
    }

    public void UpstreamTimeInvalid()
    {
        lock (_lock)
        {
            _invalidUpstreamTimes++;
            _counters?.Add(_counterPrefix + "invalidUpstreamTimes");
        }
    }

    public InputReadSnapshot Snapshot()
    {
        lock (_lock)
        {
            var health = (_readsCompleted + _readsFailed) == 0 ? InputReadHealth.NotRead
                : _offline ? InputReadHealth.Offline
                : _lastReadFailed ? InputReadHealth.Failing
                : _lastFailedFieldCount > 0 ? InputReadHealth.PartialFailure
                : InputReadHealth.Normal;
            return new InputReadSnapshot(
                DeviceKey,
                InputKey,
                health,
                _lastSuccessAt,
                _lastFailureAt,
                _lastError,
                _consecutiveFailures,
                _readsCompleted,
                _readsFailed,
                _cyclesSkipped,
                _lastSkippedAt,
                _dataRejected,
                _invalidUpstreamTimes);
        }
    }
}

/// <summary>读取状态的快照，供状态查询与测试使用。</summary>
public sealed record InputReadSnapshot(
    string DeviceKey,
    string InputKey,
    InputReadHealth Health,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    int ConsecutiveFailures,
    long ReadsCompleted,
    long ReadsFailed,
    long CyclesSkipped,
    DateTimeOffset? LastSkippedAt,
    long DataRejected,
    long InvalidUpstreamTimes);

/// <summary>数据来源的读取健康状况（功能需求 §7.2）。</summary>
public enum InputReadHealth
{
    /// <summary>尚未完成过读取。</summary>
    NotRead,

    /// <summary>最近一次读取全部成功。</summary>
    Normal,

    /// <summary>最近一次读取有字段失败。</summary>
    PartialFailure,

    /// <summary>最近一次整组读取失败，尚未达到离线判定。</summary>
    Failing,

    /// <summary>连续 3 个周期整体读取失败。</summary>
    Offline,
}

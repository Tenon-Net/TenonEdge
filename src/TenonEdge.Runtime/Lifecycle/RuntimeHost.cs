using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 运行时（总设计 §7.4、§8.1）：进程中唯一的一套运行单元，由内部的监督者管理。宿主中的协调者决定激活哪个版本，并负责配置库中的版本元数据。
/// <list type="bullet">
/// <item><see cref="Prepare"/>：按运行计划只构建、不启动——每个输入单元得到自己的有界通道与读取状态，构建失败的单元记下原因。</item>
/// <item><see cref="StartCore"/>：启动发送缓存写者。“运行失败”时同样启动，输出继续清空队列（总设计 §7.4）。</item>
/// <item><see cref="ActivateAsync"/>：启动输入、分发器与按目标划分的发送者；构建失败的只告警，单实例规则由监督者执行。</item>
/// <item><see cref="StopAsync"/>：停止输入，排空分发器，写者最后提交，再取消在途发送并释放协议客户端。</item>
/// </list>
/// 本批每个进程只激活一次；应用新版本时的静默与切换在 T3-06 实现。
/// </summary>
public sealed partial class RuntimeHost : IAsyncDisposable
{
    /// <summary>输入单元种类的前缀；业务标识为 deviceKey/inputKey 两段。</summary>
    public const string InputKindPrefix = "input.";

    private readonly Dictionary<string, IInputUnitFactory> _inputFactories;
    private readonly Dictionary<string, IOutputAdapterFactory> _outputFactories;
    private readonly List<(Channel<EdgeMessage> Channel, InputDispatcherState State)> _dispatchers = [];
    private readonly DeliveryCache _deliveries;
    private readonly IClockSyncStatus _clock;
    private readonly IRuntimeEventSink _events;
    private readonly TimeProvider _time;
    private readonly RuntimeOptions _options;
    private readonly ILogger<RuntimeHost> _logger;
    private readonly UnitSupervisor _supervisor;
    private readonly Func<double> _random;
    private readonly IOutputControl? _controls;
    private readonly Lock _lock = new();
    private readonly List<RuntimeUnitStatus> _notStarted = [];
    private readonly Dictionary<RuntimeUnitId, ChannelReader<EdgeMessage>> _inputChannels = [];

    public InFlightBudget InFlight { get; }

    private bool _coreStarted;
    private long? _activeRevision;
    private Task? _stopping;

    public RuntimeHost(
        IEnumerable<IInputUnitFactory> inputFactories,
        DeliveryCache deliveries,
        IClockSyncStatus clock,
        IRuntimeEventSink events,
        TimeProvider time,
        RuntimeOptions options,
        ILogger<RuntimeHost> logger,
        IEnumerable<IOutputAdapterFactory> outputFactories, IOutputControl controls)
        : this(inputFactories, deliveries, clock, events, time, options, logger, random: null, outputFactories, controls)
    {
    }

    /// <param name="random">重启抖动使用的随机数；测试中可以固定。</param>
    internal RuntimeHost(
        IEnumerable<IInputUnitFactory> inputFactories,
        DeliveryCache deliveries,
        IClockSyncStatus clock,
        IRuntimeEventSink events,
        TimeProvider time,
        RuntimeOptions options,
        ILogger<RuntimeHost> logger,
        Func<double>? random,
        IEnumerable<IOutputAdapterFactory>? outputFactories = null, IOutputControl? controls = null)
    {
        _random = random ?? Random.Shared.NextDouble;
        _controls = controls;
        ArgumentNullException.ThrowIfNull(inputFactories);
        _inputFactories = new Dictionary<string, IInputUnitFactory>(StringComparer.Ordinal);
        foreach (var factory in inputFactories)
        {
            if (!factory.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"输入工厂的种类 {factory.Kind} 应以 {InputKindPrefix} 开头。", nameof(inputFactories));
            }

            if (!_inputFactories.TryAdd(factory.Kind, factory))
            {
                throw new ArgumentException($"单元种类 {factory.Kind} 注册了多个工厂。", nameof(inputFactories));
            }
        }

        _outputFactories = (outputFactories ?? []).ToDictionary(factory => factory.Kind, StringComparer.Ordinal);
        _deliveries = deliveries;
        _clock = clock;
        _events = events;
        _time = time;
        _options = options.Validate();
        InFlight = new InFlightBudget(options.InFlightBytes);
        _logger = logger;
        _supervisor = new UnitSupervisor(_options, time, events, logger, random);
    }

    /// <summary>已激活的配置版本；尚未激活时为空。</summary>
    public long? ActiveRevision
    {
        get
        {
            lock (_lock)
            {
                return _activeRevision;
            }
        }
    }

    public DeliveryCache Deliveries => _deliveries;

    /// <summary>宿主确认没有有效配置时调用；损坏/启动保护中的有效配置不能当成目标已删除。</summary>
    public async Task QuarantineUnconfiguredTargetsAsync(CancellationToken token)
    {
        lock (_lock)
        {
            if (!_coreStarted || _activeRevision is not null)
            {
                throw new InvalidOperationException("只有写者已启动且尚未激活计划时才能核对无配置目标。");
            }
        }

        var result = await _deliveries.ReconcileAsync(new Dictionary<long, string>(), true, token);
        if (!result.Committed)
        {
            throw new DeliveryStoreException("未配置目标的隔离状态未提交，修复存储后重启以重新核对。");
        }
    }

    /// <summary>
    /// 只构建、不启动（总设计 §7.4 第 2 步）：不做外部 I/O，不生成消息。返回的计划中列出构建失败的单元及原因。
    /// </summary>
    public PreparedPlan Prepare(RuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var units = new List<PreparedUnit>(plan.Units.Length);
        foreach (var unit in plan.Units.Where(unit => !unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal)))
        {
            units.Add(PrepareUnit(unit));
        }

        var outputs = new List<PreparedOutput>();
        foreach (var unit in plan.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal)))
        {
            try
            {
                if (!_outputFactories.TryGetValue(unit.Id.Kind, out var factory))
                {
                    throw new ArgumentException("未注册输出模块。");
                }

                var target = factory.Build(unit);
                if (target.Id != unit.Id)
                {
                    throw new ArgumentException("输出工厂标识与计划不一致。");
                }

                if (outputs.Any(output => output.Target.ConnectionId == target.ConnectionId))
                {
                    throw new ArgumentException("同一连接不能定义多个输出单元。");
                }

                if (outputs.Any(output => output.Target.Recipient == target.Recipient && output.Target.ClientId == target.ClientId))
                {
                    throw new ArgumentException("同一 Broker 的 ClientId 冲突。");
                }

                foreach (var input in target.Inputs)
                {
                    if (!units.Any(prepared => prepared.Id.Key == input && prepared.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal)))
                    {
                        throw new ArgumentException("输出引用了不存在的输入。");
                    }

                    if (outputs.Count(output => output.Target.Inputs.Contains(input, StringComparer.Ordinal)) >= 32)
                    {
                        throw new ArgumentException("单输入最多 32 条投递路径。");
                    }
                }

                var state = new OutputSenderState();
                IRuntimeUnit BuildSender() => new OutputSenderUnit(target, state, _deliveries, _time, _events,
                    new RestartBackoff(_options.InitialRestartDelay, _options.MaximumRestartDelay, _options.RestartJitter, _random), _controls);
                outputs.Add(new PreparedOutput(target));
                units.Add(new PreparedUnit(unit.Id, BuildSender(), null, BuildSender, null, null));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                units.Add(new PreparedUnit(unit.Id, null, exception.Message, null, null, null));
            }
        }

        return new PreparedPlan(plan.Revision, units, outputs);
    }

    /// <summary>启动发送缓存写者；重复调用无效。</summary>
    public void StartCore()
    {
        lock (_lock)
        {
            ThrowIfStopping();
            if (_coreStarted)
            {
                return;
            }

            _coreStarted = true;
        }

        var writer = CreateWriter();
        if (!_supervisor.TryStart(writer, CreateWriter, reads: null))
        {
            throw new InvalidOperationException("发送缓存写者已经在运行。");
        }
    }

    /// <summary>
    /// 激活一个已构建的计划：启动构建成功的输入与输出，并连接输入的分发器。构建失败的单元不启动，记运行事件并在状态中显示原因。
    /// 本批每个进程只激活一次。
    /// </summary>
    public async Task ActivateAsync(PreparedPlan prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_lock)
        {
            ThrowIfStopping();
            if (_activeRevision is not null)
            {
                throw new InvalidOperationException($"运行时已激活版本 r{_activeRevision}；应用新版本的静默与切换在 T3-06 实现。");
            }

            // 已释放或已激活过的计划在这里被拒绝，不改变运行时的状态。
            prepared.HandOver();
            _activeRevision = prepared.Revision;
        }

        if (_coreStarted)
        {
            var reconciled = await _deliveries.ReconcileAsync(prepared.Outputs.ToDictionary(output => output.Target.ConnectionId,
                output => output.Target.CurrentFingerprint), quarantineMissing: prepared.Failures.All(failure => !failure.Id.Kind.StartsWith("output.", StringComparison.Ordinal)), CancellationToken.None);
            if (!reconciled.Committed)
            {
                foreach (var unit in prepared.Units)
                {
                    if (unit.Instance is not null)
                    { await unit.Instance.DisposeAsync(); }
                }

                lock (_lock)
                { _activeRevision = null; }
                throw new DeliveryStoreException("启动目标隔离未提交，运行计划没有启动；修复存储后重启以重新核对。");
            }
        }

        foreach (var input in prepared.Units.Where(unit => unit.Channel is BudgetedInputChannel))
        {
            var channel = (BudgetedInputChannel)input.Channel!;
            var paths = prepared.Outputs.Count(output => output.Target.Inputs.Contains(input.Id.Key, StringComparer.Ordinal));
            // 冻结 payload 拷贝及组装暂存各一份；保守预留，不代表实际堆占用。
            channel.OutputReservationBytes = paths * 2L * DeliveryDraft.MaximumPayloadBytes;
            channel.ReserveSerializationWorkspace = paths > 0;
        }

        foreach (var unit in prepared.Units)
        {
            if (unit.Instance is null)
            {
                RecordNotStarted(unit.Id, unit.Error ?? "构建失败。", unit.Reads);
                LogUnitNotStarted(_logger, unit.Id.ToString(), unit.Error ?? string.Empty);
                _events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.UnitBuildFailed, unit.Id.ToString(), $"运行单元没有启动：{unit.Error}");
                continue;
            }

            if (!_supervisor.TryStart(unit.Instance, unit.Rebuild!, unit.Reads))
            {
                const string reason = "同一单元的上一个实例仍在运行，没有启动新实例（单实例规则）。";
                RecordNotStarted(unit.Id, reason, unit.Reads);
                _events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.UnitBuildFailed, unit.Id.ToString(), reason);
                await unit.Instance.DisposeAsync();
                continue;
            }

            if (unit.Channel is not null)
            {
                lock (_lock)
                {
                    _inputChannels[unit.Id] = unit.Channel.Reader;
                }

                var targets = prepared.Outputs.Select(output => output.Target).Where(target => target.Inputs.Contains(unit.Id.Key, StringComparer.Ordinal)).ToArray();
                if (targets.Length > 0)
                {
                    var state = new InputDispatcherState();
                    IRuntimeUnit BuildDispatcher() => new InputDispatcherUnit(unit.Id, ((BudgetedInputChannel)unit.Channel).HoldingReader, targets, state, prepared.Revision, _deliveries, _time, _events, (BudgetedInputChannel)unit.Channel);
                    _dispatchers.Add((unit.Channel, state));
                    _supervisor.TryStart(BuildDispatcher(), BuildDispatcher, reads: null);
                }
            }
        }

        LogActivated(_logger, prepared.Revision, prepared.Units.Count - prepared.Failures.Count, prepared.Failures.Count);
    }

    /// <summary>
    /// 停机顺序（总设计 §8.1）：停止输入并排空通道，写者最后提交，再停止发送；尚未提交的请求报告未接管。
    /// 每一步按 <see cref="RuntimeOptions.UnitStopTimeout"/> 等待，到期仍未退出的单元标为“停止超时”。重复调用等待同一次停止。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? owner = null;
        Task stopping;
        lock (_lock)
        {
            if (_stopping is null)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopping = owner.Task;
            }

            stopping = _stopping;
        }

        if (owner is null)
        {
            await stopping;
            return;
        }

        try
        {
            await StopCoreAsync(cancellationToken);
            owner.SetResult();
        }
        catch (Exception exception)
        {
            owner.SetException(exception);
            throw;
        }
    }

    public RuntimeStatusReport GetStatus()
    {
        lock (_lock)
        {
            return new RuntimeStatusReport(_activeRevision, [.. _supervisor.Snapshot(), .. _notStarted]);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    /// <summary>输入通道的读取端：供测试与 T1-04 的分发器使用。</summary>
    internal ChannelReader<EdgeMessage>? GetInputChannel(RuntimeUnitId id)
    {
        lock (_lock)
        {
            return _inputChannels.GetValueOrDefault(id);
        }
    }

    private PreparedUnit PrepareUnit(RuntimeUnitPlan unit)
    {
        var id = unit.Id;
        if (!id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal))
        {
            return Failed(id, $"本版本不支持单元种类 {id.Kind}：只支持已注册的输入和输出模块。");
        }

        if (!_inputFactories.TryGetValue(id.Kind, out var factory))
        {
            return Failed(id, $"没有注册单元种类 {id.Kind}（缺少模块）。");
        }

        var segments = id.KeySegments;
        if (segments.Count != 2)
        {
            return Failed(id, "输入单元的业务标识应为 deviceKey/inputKey 两段。");
        }

        var channel = new BudgetedInputChannel(_options.InputChannelCapacity, InFlight);
        var reads = new InputReadStatus(segments[0], segments[1], _events, _deliveries.PendingCounters);
        var context = new InputUnitContext(segments[0], segments[1], _time, _clock, reads, (InputMessageWriter)channel.Writer);
        IRuntimeUnit Build()
        {
            var instance = factory.Build(unit, context);
            if (instance.Id != id)
            {
                throw new RuntimeUnitBuildException(id, $"工厂返回的单元标识为 {instance.Id}，与计划不符。");
            }

            return instance;
        }

        try
        {
            return new PreparedUnit(id, Build(), null, Build, channel, reads);
        }
        catch (RuntimeUnitBuildException exception)
        {
            return Failed(id, exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogUnexpectedBuildError(_logger, exception, id.ToString());
            return Failed(id, $"构建时发生未预期的错误：{exception.Message}");
        }

        static PreparedUnit Failed(RuntimeUnitId id, string error) => new(id, null, error, null, null, null);
    }

    private DeliveryWriterUnit CreateWriter() => new(_deliveries, _time, _events, _logger);

    private void RecordNotStarted(RuntimeUnitId id, string reason, InputReadStatus? reads)
    {
        lock (_lock)
        {
            _notStarted.Add(new RuntimeUnitStatus(id, RuntimeUnitState.BuildFailed, 0, reason, _time.GetUtcNow(), null, reads?.Snapshot()));
        }
    }

    private void ThrowIfStopping()
    {
        if (_stopping is not null)
        {
            throw new InvalidOperationException("运行时正在停止或已经停止。");
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        // 每一步出错都只记日志，后面的步骤照常执行：停机顺序不因某一步的意外异常而中断。
        var timeout = _options.UnitStopTimeout;
        var inputsTimedOut = await StopStepAsync("停止输入", () => _supervisor.StopAsync(id => id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal), timeout, cancellationToken));

        // 输入完全停止后封闭通道；分发器处理已进入通道的消息并等待每次提交结果。
        foreach (var dispatcher in _dispatchers)
        {
            dispatcher.Channel.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(_dispatchers.Select(dispatcher => dispatcher.State.Drained.Task)).WaitAsync(timeout, _time, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _events.Record(RuntimeEventSeverity.Error, "dispatcher.drainTimedOut", null, "输入通道排空超时，未接管的内存数据可能丢失。");
        }

        var dispatcherTimedOut = await StopStepAsync("停止分发器", () => _supervisor.StopAsync(id => id.Kind == "delivery.dispatcher", timeout, cancellationToken));
        _deliveries.CompleteAdding();
        // 写者收到停止请求后不再等待提交间隔，提交请求队列中已有的请求后退出；之后请求队列不再接受新请求，
        // 停止之后才到达的请求一律报告失败。
        var writerTimedOut = await StopStepAsync("停止发送缓存写者", () => _supervisor.StopAsync(id => id == DeliveryCache.WriterId, timeout, cancellationToken));
        var senderTimedOut = await StopStepAsync("停止输出发送者", () => _supervisor.StopAsync(id => id.Kind.StartsWith("output.", StringComparison.Ordinal), timeout, cancellationToken));
        var failed = _deliveries.FailRemaining("发送缓存已停止，请求没有提交。");
        if (inputsTimedOut.Count == 0 && dispatcherTimedOut.Count == 0 && writerTimedOut.Count == 0)
        {
            // 写者已退出、请求已完成，才能释放停止途中仍由分发器持有的额度。
            foreach (var dispatcher in _dispatchers)
            {
                if (dispatcher.State.Message is { } message && dispatcher.Channel is BudgetedInputChannel channel)
                {
                    channel.Release(message);
                    dispatcher.State.Message = null;
                    dispatcher.State.Drafts = null;
                }
            }

            foreach (var reader in _inputChannels.Values)
            {
                while (reader.TryRead(out _)) { }
            }
        }

        _deliveries.Store.ClearReadPool();
        LogStopped(_logger, inputsTimedOut.Count + dispatcherTimedOut.Count + writerTimedOut.Count + senderTimedOut.Count, failed);
    }

    private async Task<IReadOnlyList<RuntimeUnitId>> StopStepAsync(string step, Func<Task<IReadOnlyList<RuntimeUnitId>>> stop)
    {
        try
        {
            return await stop();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogStopStepFailed(_logger, exception, step);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "运行时已激活版本 r{Revision}：启动 {Started} 个计划单元，{Failed} 个因构建失败没有启动。")]
    private static partial void LogActivated(ILogger logger, long revision, int started, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "运行单元 {UnitId} 没有启动：{Reason}")]
    private static partial void LogUnitNotStarted(ILogger logger, string unitId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "构建运行单元 {UnitId} 时发生未预期的错误。")]
    private static partial void LogUnexpectedBuildError(ILogger logger, Exception exception, string unitId);

    [LoggerMessage(Level = LogLevel.Error, Message = "停机步骤“{Step}”出错，继续执行后面的步骤。")]
    private static partial void LogStopStepFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "运行时已停止：{TimedOut} 个单元停止超时，{Failed} 条记录的写入请求未能提交。")]
    private static partial void LogStopped(ILogger logger, int timedOut, int failed);
}

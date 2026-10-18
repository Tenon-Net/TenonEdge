using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Outputs;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 运行时（总设计 §7.4、§8.1）：进程中唯一的一套运行单元，由内部的监督者管理。宿主中的协调者决定激活哪个版本，并负责配置库中的版本元数据。
/// <list type="bullet">
/// <item><see cref="Prepare"/>：按运行计划只构建、不启动——每个输入单元得到自己的有界通道与读取状态，构建失败的单元记下原因。</item>
/// <item><see cref="StartCore"/>：启动发送缓存写者。“运行失败”时同样启动，输出继续清空队列（总设计 §7.4）。</item>
/// <item><see cref="ActivateAsync"/>：启动输入、分发器与按目标划分的发送者；构建失败的只告警，单实例规则由监督者执行。</item>
/// <item><see cref="StopAsync"/>：停止输入，排空分发器，写者最后提交，再取消在途发送并释放协议客户端。</item>
/// </list>
/// ActivateAsync 仅用于初次激活；在线应用由宿主提交配置之后调用 ActivateCommittedAsync。
/// </summary>
public sealed partial class RuntimeHost : IAsyncDisposable
{
    /// <summary>输入单元种类的前缀；业务标识为 deviceKey/inputKey 两段。</summary>
    public const string InputKindPrefix = "input.";

    private readonly Dictionary<string, IInputUnitFactory> _inputFactories;
    private readonly Dictionary<string, IOutputAdapterFactory> _outputFactories;
    private readonly IRuntimePlanAdapterProvider[] _planAdapterProviders;
    private readonly List<(RuntimeUnitId InputId, Channel<EdgeMessage> Channel, InputDispatcherState State)> _dispatchers = [];
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
    private readonly Dictionary<RuntimeUnitId, BudgetedInputChannel> _inputChannels = [];

    public InFlightBudget InFlight { get; }
    /// <summary>进程内唯一的诊断消息缓存，独立于界面订阅；不代表来源当前可取样。</summary>
    public RuntimeMessageCache Messages { get; }
    public CurrentValueCache CurrentValues { get; }

    private bool _coreStarted;
    private long? _activeRevision;
    private Task? _stopping;
    private TaskCompletionSource? _operation;
    private IReadOnlyList<PreparedOutput> _activeOutputs = [];
    private RuntimePlan? _activeDefinition;
    private readonly Dictionary<RuntimeUnitId, PreparedUnit> _activeInputs = [];
    private readonly Dictionary<RuntimeUnitId, RuntimeUnitId> _outputRecoveryGroups = [];

    public RuntimeHost(
        IEnumerable<IInputUnitFactory> inputFactories,
        DeliveryCache deliveries,
        IClockSyncStatus clock,
        IRuntimeEventSink events,
        TimeProvider time,
        RuntimeOptions options,
        ILogger<RuntimeHost> logger,
        IEnumerable<IOutputAdapterFactory> outputFactories, IOutputControl controls,
        IEnumerable<IRuntimePlanAdapterProvider>? planAdapterProviders = null, RuntimeMessageCache? messages = null, InFlightBudget? inFlight = null, InputChannelLimit? inputChannels = null, TransformCatalog? transforms = null, InputModuleCatalog? inputModules = null)
        : this(inputFactories, deliveries, clock, events, time, options, logger, random: null, outputFactories, controls, planAdapterProviders, messages, inFlight, inputChannels, transforms, inputModules)
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
        IEnumerable<IOutputAdapterFactory>? outputFactories = null, IOutputControl? controls = null,
        IEnumerable<IRuntimePlanAdapterProvider>? planAdapterProviders = null, RuntimeMessageCache? messages = null, InFlightBudget? inFlight = null, InputChannelLimit? inputChannels = null, TransformCatalog? transforms = null, InputModuleCatalog? inputModules = null)
    {
        Transforms = transforms ?? TransformCatalog.BuiltIn;
        InputModules = inputModules ?? InputModuleCatalog.Empty;
        _random = random ?? Random.Shared.NextDouble;
        _controls = controls;
        _planAdapterProviders = (planAdapterProviders ?? []).ToArray();
        ArgumentNullException.ThrowIfNull(inputFactories);
        _inputFactories = new Dictionary<string, IInputUnitFactory>(StringComparer.Ordinal);
        foreach (var factory in inputFactories.Concat(InputModules.Factories))
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
        InFlight = inFlight ?? new InFlightBudget(options.InFlightBytes, options.InputBufferBytes);
        Messages = messages ?? new RuntimeMessageCache(time);
        InputChannels = inputChannels ?? new InputChannelLimit(options.InputChannelCapacity);
        CurrentValues = new CurrentValueCache(time);
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

    public TransformCatalog Transforms { get; }
    public InputModuleCatalog InputModules { get; }
    public DeliveryCache Deliveries => _deliveries;
    public InputChannelLimit InputChannels { get; }

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
    public PreparedPlan Prepare(RuntimePlan plan) => PrepareCore(plan, null);

    private PreparedPlan PrepareCore(RuntimePlan plan, IReadOnlyDictionary<long, OutputSenderState>? senderStates,
        HashSet<RuntimeUnitId>? selectedInputs = null, Dictionary<RuntimeUnitId, PreparedOutput>? retainedOutputs = null, HashSet<RuntimeUnitId>? selectedOutputs = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var inputFactories = new Dictionary<string, IInputUnitFactory>(_inputFactories, StringComparer.Ordinal);
        var outputFactories = new Dictionary<string, IOutputAdapterFactory>(_outputFactories, StringComparer.Ordinal);
        foreach (var provider in _planAdapterProviders)
        {
            var adapters = provider.Create();
            foreach (var factory in adapters.Inputs)
            {
                if (!factory.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal) || !inputFactories.TryAdd(factory.Kind, factory))
                { throw new ArgumentException("计划输入工厂种类无效或重复注册。"); }
            }
            foreach (var factory in adapters.Outputs)
            {
                if (!factory.Kind.StartsWith("output.", StringComparison.Ordinal) || !outputFactories.TryAdd(factory.Kind, factory))
                { throw new ArgumentException("计划输出工厂种类无效或重复注册。"); }
            }
        }
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var units = new List<PreparedUnit>(plan.Units.Length);
        foreach (var unit in plan.Units.Where(unit => !unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal)))
        {
            if (selectedInputs is null || selectedInputs.Contains(unit.Id))
            { units.Add(PrepareUnit(unit, activation.Task, inputFactories, plan.Revision)); }
        }

        var outputs = new List<PreparedOutput>();
        foreach (var unit in plan.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal)))
        {
            if (selectedOutputs is not null && !selectedOutputs.Contains(unit.Id))
            {
                if (retainedOutputs?.TryGetValue(unit.Id, out var retained) == true)
                { outputs.Add(retained); }
                continue;
            }
            RuntimeUnitId? recoveryGroup = null;
            try
            {
                if (!outputFactories.TryGetValue(unit.Id.Kind, out var factory))
                {
                    throw new PlanValidationException("未注册输出模块。");
                }

                var candidateGroup = factory.RecoveryGroup(unit);
                recoveryGroup = candidateGroup is not null && units.Any(input => input.RecoveryGroup == candidateGroup) ? candidateGroup : null;
                var mapping = Transforms.ParsePipeline(unit.Configuration);
                if (mapping?.WorkingReservationBytes > _options.InFlightBytes)
                { throw new PlanValidationException("转换工作预算超过全局在途预算。"); }
                var target = factory.Build(unit);
                if (target.QuiesceWithInputs && recoveryGroup is null)
                { throw new PlanValidationException("共用输入会话的输出必须声明对应的恢复分组。"); }
                if (target.FormattingReservationBytes < 2L * DeliveryDraft.MaximumPayloadBytes || target.FormattingReservationBytes > _options.InFlightBytes)
                { throw new PlanValidationException("输出格式化预算无效或超过全局在途预算。"); }
                if (target.MinimumSerializationReservationBytes < 0 || target.MinimumSerializationReservationBytes > _options.InFlightBytes)
                { throw new PlanValidationException("输出编码暂存预算无效或超过全局在途预算。"); }
                if (target.Id != unit.Id)
                {
                    throw new PlanValidationException("输出工厂标识与计划不一致。");
                }

                var shared = outputs.FirstOrDefault(output => output.Target.ConnectionId == target.ConnectionId);
                if (shared is not null && (shared.RecoveryGroup != recoveryGroup || !shared.Target.CanShareSenderWith(target)))
                {
                    throw new PlanValidationException("同一连接的输出参数不一致或协议尚不支持共享发送者。");
                }

                if (target.ClientId is not null && outputs.Any(output => output.Target.ConnectionId != target.ConnectionId && output.Target.Recipient == target.Recipient && output.Target.ClientId == target.ClientId))
                {
                    throw new PlanValidationException("同一 Broker 的 ClientId 冲突。");
                }

                foreach (var input in target.Inputs)
                {
                    if (!plan.Units.Any(definition => definition.Id.Key == input && definition.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal)))
                    {
                        throw new PlanValidationException("输出引用了不存在的输入。");
                    }

                    if (outputs.Count(output => output.Target.Inputs.Contains(input, StringComparer.Ordinal)) >= 32)
                    {
                        throw new PlanValidationException("单输入最多 32 条投递路径。");
                    }
                }

                var state = shared?.State ?? senderStates?.GetValueOrDefault(target.ConnectionId) ?? new OutputSenderState();
                IRuntimeUnit BuildSender() => new OutputSenderUnit(target, state, _deliveries, _time, _events,
                    new RestartBackoff(_options.InitialRestartDelay, _options.MaximumRestartDelay, _options.RestartJitter, _random), _controls);
                outputs.Add(new PreparedOutput(target, mapping, state, recoveryGroup, candidateGroup));
                if (shared is null)
                { units.Add(new PreparedUnit(unit.Id, BuildSender(), null, BuildSender, null, null, recoveryGroup)); }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                units.Add(new PreparedUnit(unit.Id, null, DescribeBuildFailure(exception), null, null, null, recoveryGroup));
            }
        }

        return new PreparedPlan(plan, units, outputs, activation);
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
    /// 仅用于初次激活；恢复原版本输入使用 RestoreInputsAsync。
    /// </summary>
    public async Task ActivateAsync(PreparedPlan prepared)
    {
        var operation = EnterOperation();
        try
        { await ActivateCoreAsync(prepared); }
        finally { CompleteOperation(operation); }
    }

    private async Task ActivateCoreAsync(PreparedPlan prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_lock)
        {
            ThrowIfStopping();
            if (_activeRevision is not null)
            {
                throw new InvalidOperationException($"运行时已激活版本 r{_activeRevision}；不能通过初次激活入口切换版本。");
            }

            // 已释放或已激活过的计划在这里被拒绝，不改变运行时的状态。
            prepared.HandOver();
            _activeRevision = prepared.Revision;
            CurrentValues.Activate(prepared.Revision, prepared.Definition.Units.Where(unit => unit.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal)).Select(unit => unit.Id));
            ActivateMessageDiagnostics(prepared.Definition);
            _deliveries.OutputTraffic.RetainTargets(prepared.Outputs.Select(output => output.Target.ConnectionId));
            _activeOutputs = prepared.Outputs;
            _activeDefinition = prepared.Definition;
            foreach (var input in prepared.Units.Where(unit => unit.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal)))
            { _activeInputs[input.Id] = input; }
            foreach (var output in prepared.Outputs.Where(output => output.RecoveryGroup is not null))
            { _outputRecoveryGroups[output.Target.Id] = output.RecoveryGroup!; }
            foreach (var output in prepared.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal) && unit.RecoveryGroup is not null))
            { _outputRecoveryGroups[output.Id] = output.RecoveryGroup!; }
        }

        try
        {
            if (_coreStarted)
            {
                var reconciled = await _deliveries.ReconcileAsync(prepared.Outputs.GroupBy(output => output.Target.ConnectionId).ToDictionary(group => group.Key,
                    group => group.First().Target.CurrentFingerprint), quarantineMissing: prepared.Failures.All(failure => !failure.Id.Kind.StartsWith("output.", StringComparison.Ordinal)), CancellationToken.None);
                if (!reconciled.Committed)
                {
                    prepared.CancelActivation();
                    foreach (var unit in prepared.Units)
                    {
                        if (unit.Instance is not null)
                        { await unit.Instance.DisposeAsync(); }
                    }

                    lock (_lock)
                    { _activeRevision = null; _activeOutputs = []; _activeDefinition = null; _messageCatalog = new(null, []); _activeInputs.Clear(); _outputRecoveryGroups.Clear(); }
                    throw new DeliveryStoreException("启动目标隔离未提交，运行计划没有启动；修复存储后重启以重新核对。");
                }
            }

            await StartPreparedUnitsAsync(prepared, []);

            LogActivated(_logger, prepared.Revision, prepared.Units.Count - prepared.Failures.Count, prepared.Failures.Count);
            prepared.CompleteActivation();
        }
        catch
        {
            prepared.CancelActivation();
            throw;
        }
    }

    private async Task StartPreparedUnitsAsync(PreparedPlan prepared, HashSet<RuntimeUnitId> retainedSenders)
    {
        foreach (var input in prepared.Units.Where(unit => unit.Channel is BudgetedInputChannel))
        {
            var channel = (BudgetedInputChannel)input.Channel!;
            var paths = prepared.Outputs.Count(output => output.Target.Inputs.Contains(input.Id.Key, StringComparer.Ordinal));
            // 按协议声明预留报文、冻结拷贝及非秘密元数据；不代表实际堆占用。
            channel.OutputReservationBytes = prepared.Outputs.Where(output => output.Target.Inputs.Contains(input.Id.Key, StringComparer.Ordinal))
                .Sum(output => output.Target.FormattingReservationBytes);
            channel.ReserveSerializationWorkspace = paths > 0;
            channel.MinimumSerializationReservationBytes = prepared.Outputs.Where(output => output.Target.Inputs.Contains(input.Id.Key, StringComparer.Ordinal))
                .Select(output => output.Target.MinimumSerializationReservationBytes).DefaultIfEmpty().Max();
            channel.MappingWorkingReservationBytes = prepared.Outputs.Where(output => output.Target.Inputs.Contains(input.Id.Key, StringComparer.Ordinal))
                .Select(output => output.Mapping?.WorkingReservationBytes ?? 0).DefaultIfEmpty().Max();
        }

        foreach (var unit in prepared.Units)
        {
            if (retainedSenders.Contains(unit.Id))
            {
                if (unit.Instance is not null)
                { await unit.Instance.DisposeAsync(); }
                continue;
            }
            if (unit.Instance is null)
            {
                RecordNotStarted(unit.Id, unit.Error ?? "构建失败。", unit.Reads);
                LogUnitNotStarted(_logger, unit.Id.ToString(), unit.Error ?? string.Empty);
                _events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.UnitBuildFailed, unit.Id.ToString(), $"运行单元没有启动：{unit.Error}");
                continue;
            }

            var senderOutput = prepared.Outputs.FirstOrDefault(output => output.Target.Id == unit.Id);
            var senderGroup = senderOutput is null ? null : SenderGroup(senderOutput.Target.ConnectionId);
            if (!_supervisor.TryStart(unit.Instance, unit.Rebuild!, unit.Reads, senderGroup))
            {
                const string reason = "同一单元或发送连接的上一个实例仍在运行或等待重建，没有启动新实例（单实例规则）。";
                RecordNotStarted(unit.Id, reason, unit.Reads);
                _events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.UnitBuildFailed, unit.Id.ToString(), reason);
                await unit.Instance.DisposeAsync();
                continue;
            }
            if (senderOutput is not null)
            {
                _senders[senderOutput.Target.ConnectionId] = senderOutput;
                _senderStates[senderOutput.Target.ConnectionId] = senderOutput.State;
                RememberSenderConnection(senderOutput);
            }

            if (unit.Channel is not null)
            {
                lock (_lock)
                {
                    _inputChannels[unit.Id] = (BudgetedInputChannel)unit.Channel;
                }

                var targets = prepared.Outputs.Select(output => output.Target).Where(target => target.Inputs.Contains(unit.Id.Key, StringComparer.Ordinal)).ToArray();
                // 无输出也持续排空：诊断捕获不应在 100 条后因无人消费而停止。
                var state = new InputDispatcherState();
                var mappings = prepared.Outputs.Where(output => output.Mapping is not null && output.Target.Inputs.Contains(unit.Id.Key, StringComparer.Ordinal))
                    .ToDictionary(output => output.Target.Id, output => output.Mapping!);
                IRuntimeUnit BuildDispatcher() => new InputDispatcherUnit(unit.Id, ((BudgetedInputChannel)unit.Channel).HoldingReader, targets, state, prepared.Revision, _deliveries, _time, _events, (BudgetedInputChannel)unit.Channel, mappings, Messages, prepared.Definition.DiagnosticObjects);
                _dispatchers.Add((unit.Id, unit.Channel, state));
                _supervisor.TryStart(BuildDispatcher(), BuildDispatcher, reads: null);
            }
        }

    }

    /// <summary>
    /// 停机顺序（总设计 §8.1）：停止输入并排空通道，写者最后提交，再停止发送；尚未提交的请求报告未接管。
    /// 每一步按 <see cref="RuntimeOptions.UnitStopTimeout"/> 等待，到期仍未退出的单元标为“停止超时”。重复调用等待同一次停止。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? owner = null;
        Task stopping;
        Task operation;
        lock (_lock)
        {
            if (_stopping is null)
            {
                _currentQuiescence = null;
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopping = owner.Task;
            }

            stopping = _stopping;
            operation = _operation?.Task ?? Task.CompletedTask;
        }

        if (owner is null)
        {
            await stopping;
            return;
        }

        try
        {
            // 已开始的本地生命周期操作由运行时拥有；先等其退出，避免停机和激活/排空交错。
            await operation;
            await CancelRecoveryAsync();
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

    /// <summary>按连接发布的发送诊断；并发枚举不会暴露生命周期正在修改的字典。</summary>
    public RuntimeOutputSender[] GetOutputSenders() => _senderStates.OrderBy(pair => pair.Key)
        .Select(pair => new RuntimeOutputSender(pair.Key, pair.Value.Diagnostics)).ToArray();

    /// <summary>仅向当前运行发送者的当前退避窗口提交可合并信号；不创建适配器或执行发送。
    /// 暂停始终由原循环优先检查，旧版本/旧退避/停止中的请求不能影响下一次退避。</summary>
    public bool RequestOutputRetry(long connectionId, long revision, string backoffVersion)
    {
        if (_controls?.Read(connectionId).ManuallyPaused == true)
        { return false; }
        lock (_lock)
        {
            if (_stopping is not null || _operation is not null || _activeRevision != revision ||
                !_senderStates.TryGetValue(connectionId, out var sender) || sender.Diagnostics.UnitId is not { } id ||
                !_supervisor.Snapshot().Any(unit => unit.Id == id && unit.State == RuntimeUnitState.Running))
            { return false; }
            return sender.RequestImmediateRetry(backoffVersion);
        }
    }

    /// <summary>供已获授权的队列操作读取有效输出身份；调用方须拥有配置应用/停机准入，不能使用客户端自报指纹。
    /// 凭证随后变化仍由原发送者逐条比较指纹，人工重试不会改写记录的身份。</summary>
    public IReadOnlyDictionary<long, string>? CaptureOutputFingerprints(long revision)
    {
        lock (_lock)
        {
            if (_stopping is not null || _operation is not null || _activeRevision != revision)
            { return null; }
            return _activeOutputs.GroupBy(output => output.Target.ConnectionId).ToDictionary(group => group.Key,
                group => group.First().Target.CurrentFingerprint);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    /// <summary>诊断捕获失败次数（不含消息内容），不改变输入投递/确认结果。</summary>
    public long InputCaptureFailures
    {
        get { lock (_lock) { return _inputChannels.Values.Sum(channel => channel.CaptureFailures); } }
    }

    /// <summary>完整样本仅在对应版本的输入正在运行时可执行；停止/退避/截断/淘汰均拒绝。</summary>
    public bool TryReadInputSample(RuntimeUnitId id, long sequence, long revision, Guid sourceMessageId,
        [NotNullWhen(true)] out EdgeMessage? message)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lock)
        {
            message = null;
            if (_stopping is not null || _activeRevision != revision || !id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal) ||
                !_supervisor.Snapshot().Any(unit => unit.Id == id && unit.State == RuntimeUnitState.Running) ||
                !Messages.TryReadComplete(id, sequence, revision, sourceMessageId, out var body))
            { return false; }
            var decoded = InputMessageSnapshot.Decode(body);
            if (decoded.SourceMessageId != sourceMessageId || id.Key != $"{decoded.DeviceKey}/{decoded.InputId}" ||
                !_supervisor.Snapshot().Any(unit => unit.Id == id && unit.State == RuntimeUnitState.Running))
            { return false; }
            message = decoded;
            return true;
        }
    }

    private PreparedUnit PrepareUnit(RuntimeUnitPlan unit, Task activation, Dictionary<string, IInputUnitFactory> factories, long revision)
    {
        var id = unit.Id;
        if (!id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal))
        {
            return Failed(id, $"本版本不支持单元种类 {id.Kind}：只支持已注册的输入和输出模块。");
        }

        if (!factories.TryGetValue(id.Kind, out var factory))
        {
            return Failed(id, $"没有注册单元种类 {id.Kind}（缺少模块）。");
        }

        var segments = id.KeySegments;
        if (segments.Count != 2)
        {
            return Failed(id, "输入单元的业务标识应为 deviceKey/inputKey 两段。");
        }

        var channel = new BudgetedInputChannel(_options.InputChannelCapacity, InFlight, message =>
        {
            try
            { CurrentValues.Capture(id, revision, message); }
            finally { Messages.CaptureInput(id, revision, message); }
        }, InputChannels)
        { UsesDurableHandoff = factory.UsesDurableHandoff };
        var reads = new InputReadStatus(segments[0], segments[1], _events, _deliveries.PendingCounters);
        var context = new InputUnitContext(segments[0], segments[1], _time, _clock, reads, (InputMessageWriter)channel.Writer, activation, _options.InputBufferBytes > 0 ? InFlight : null);
        IRuntimeUnit Build()
        {
            var instance = factory.Build(unit, context);
            if (instance.Id != id)
            {
                throw new PlanValidationException("输入工厂标识与计划不一致。");
            }

            return instance;
        }

        var group = id;
        try
        {
            group = factory.RecoveryGroup(unit) ?? throw new PlanValidationException("输入恢复分组不能为空。");
            return new PreparedUnit(id, Build(), null, Build, channel, reads, group);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is not RuntimeUnitBuildException and not PlanValidationException)
            { LogUnexpectedBuildError(_logger, id.ToString()); }
            return new PreparedUnit(id, null, DescribeBuildFailure(exception), null, null, null, group);
        }

        static PreparedUnit Failed(RuntimeUnitId id, string error) => new(id, null, error, null, null, null);
    }

    // 只有本类生成的固定验证说明可信；适配器、映射解析器与凭证提供程序的异常可能携带秘密。
    private sealed class PlanValidationException(string message) : Exception(message);

    private static string DescribeBuildFailure(Exception exception) => exception switch
    {
        PlanValidationException validation => validation.Message,
        RuntimeUnitBuildException => "运行单元构建失败，请检查配置和凭证。",
        _ => "运行单元构建失败，请检查配置、模块和依赖。",
    };

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
        // 只有原发送循环确实不再占用时才收回跨提交屏障；停止异常/超时不能让旧地址重新取队列。
        if (_applicationSenderPauses.Keys.All(id => !_supervisor.IsGroupOccupied(SenderGroup(id))))
        { ReleaseApplicationSenderPauses(); }
        var failed = _deliveries.FailRemaining("发送缓存已停止，请求没有提交。");
        if (inputsTimedOut.Count == 0 && dispatcherTimedOut.Count == 0 && writerTimedOut.Count == 0)
        {
            // 写者已退出、请求已完成，才能释放停止途中仍由分发器持有的额度。
            foreach (var dispatcher in _dispatchers)
            {
                dispatcher.State.Publication?.Dispose();
                dispatcher.State.Publication = null;
                if (dispatcher.State.Message is { } message && dispatcher.Channel is BudgetedInputChannel channel)
                {
                    channel.Release(message);
                    dispatcher.State.Message = null;
                    dispatcher.State.Drafts = null;
                }
            }

            foreach (var reader in _inputChannels.Values)
            {
                while (reader.Reader.TryRead(out _))
                { }
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
    private static partial void LogUnexpectedBuildError(ILogger logger, string unitId);

    [LoggerMessage(Level = LogLevel.Error, Message = "停机步骤“{Step}”出错，继续执行后面的步骤。")]
    private static partial void LogStopStepFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "运行时已停止：{TimedOut} 个单元停止超时，{Failed} 条记录的写入请求未能提交。")]
    private static partial void LogStopped(ILogger logger, int timedOut, int failed);
}

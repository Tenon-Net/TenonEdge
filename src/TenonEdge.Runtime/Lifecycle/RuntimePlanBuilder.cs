using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Outputs;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 把冻结的运行计划构建成尚未启动的单元、通道和重建委托。共享宿主的缓存、预算及输出控制，
/// 不创建第二套运行时，也不拥有监督者、活跃版本或停机任务。准备结果由调用方释放或交给监督者。
/// </summary>
internal sealed partial class RuntimePlanBuilder(
    IEnumerable<IInputUnitFactory> inputFactories, IEnumerable<IOutputAdapterFactory> outputFactories,
    IEnumerable<IRuntimePlanAdapterProvider> planAdapterProviders,
    InputModuleCatalog inputModules, OutputModuleCatalog outputModules, TransformCatalog transforms,
    DeliveryCache deliveries, IClockSyncStatus clock, IRuntimeEventSink events, TimeProvider time,
    RuntimeOptions options, ILogger logger, Func<double>? random, IOutputControl? controls,
    InFlightBudget inFlight, InputChannelLimit inputChannels, RuntimeMessageCache messages, CurrentValueCache currentValues)
{
    private readonly Dictionary<string, IInputUnitFactory> _inputFactories = RegisterInputs(inputFactories, inputModules);
    private readonly Dictionary<string, IOutputAdapterFactory> _outputFactories = outputFactories.Concat(outputModules.Factories)
        .ToDictionary(factory => factory.Kind, StringComparer.Ordinal);
    private readonly IRuntimePlanAdapterProvider[] _planAdapterProviders = planAdapterProviders.ToArray();
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    public PreparedPlan Prepare(RuntimePlan plan, IReadOnlyDictionary<long, OutputSenderState>? senderStates = null,
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
                if (!factory.Kind.StartsWith(RuntimeHost.InputKindPrefix, StringComparison.Ordinal) || !inputFactories.TryAdd(factory.Kind, factory))
                { throw new ArgumentException("计划输入工厂种类无效或重复注册。"); }
            }
            foreach (var factory in adapters.Outputs)
            {
                if (!factory.Kind.StartsWith("output.", StringComparison.Ordinal) || !outputFactories.TryAdd(factory.Kind, factory))
                { throw new ArgumentException("计划输出工厂种类无效或重复注册。"); }
            }
        }
        // 每次准备只创建一套协议工厂；模块委托与监督者重建始终使用本计划冻结的工厂视图。
        var protocolFactories = inputFactories.Where(pair => !inputModules.Modules.Any(module => module.Kind == pair.Key))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var factory in inputModules.BindPlanFactories(protocolFactories))
        { inputFactories[factory.Kind] = factory; }
        var outputProtocols = outputFactories.Where(pair => !outputModules.Modules.Any(module => module.Kind == pair.Key))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var factory in outputModules.BindPlanFactories(outputProtocols))
        { outputFactories[factory.Kind] = factory; }
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
                var mapping = transforms.ParsePipeline(unit.Configuration);
                if (mapping?.WorkingReservationBytes > options.InFlightBytes)
                { throw new PlanValidationException("转换工作预算超过全局在途预算。"); }
                var target = factory.Build(unit);
                if (target.QuiesceWithInputs && recoveryGroup is null)
                { throw new PlanValidationException("共用输入会话的输出必须声明对应的恢复分组。"); }
                if (target.FormattingReservationBytes < 2L * DeliveryDraft.MaximumPayloadBytes || target.FormattingReservationBytes > options.InFlightBytes)
                { throw new PlanValidationException("输出格式化预算无效或超过全局在途预算。"); }
                if (target.MinimumSerializationReservationBytes < 0 || target.MinimumSerializationReservationBytes > options.InFlightBytes)
                { throw new PlanValidationException("输出编码暂存预算无效或超过全局在途预算。"); }
                if (target.Id != unit.Id)
                {
                    throw new PlanValidationException("输出工厂标识与计划不一致。");
                }

                var protocolKind = OutputModuleCatalog.ReadProtocol(unit)?.Id.Kind ?? unit.Id.Kind;
                var shared = outputs.FirstOrDefault(output => output.Target.ConnectionId == target.ConnectionId);
                if (shared is not null && (shared.ProtocolKind != protocolKind || shared.RecoveryGroup != recoveryGroup || !shared.Target.CanShareSenderWith(target)))
                {
                    throw new PlanValidationException("同一连接的输出参数不一致或协议尚不支持共享发送者。");
                }

                if (target.ClientId is not null && outputs.Any(output => output.Target.ConnectionId != target.ConnectionId && output.Target.Recipient == target.Recipient && output.Target.ClientId == target.ClientId))
                {
                    throw new PlanValidationException("同一 Broker 的 ClientId 冲突。");
                }

                foreach (var input in target.Inputs)
                {
                    if (!plan.Units.Any(definition => definition.Id.Key == input && definition.Id.Kind.StartsWith(RuntimeHost.InputKindPrefix, StringComparison.Ordinal)))
                    {
                        throw new PlanValidationException("输出引用了不存在的输入。");
                    }

                    if (outputs.Count(output => output.Target.Inputs.Contains(input, StringComparer.Ordinal)) >= 32)
                    {
                        throw new PlanValidationException("单输入最多 32 条投递路径。");
                    }
                }

                var state = shared?.State ?? senderStates?.GetValueOrDefault(target.ConnectionId) ?? new OutputSenderState();
                IRuntimeUnit BuildSender() => CreateSender(target, state);
                outputs.Add(new PreparedOutput(target, protocolKind, mapping, state, recoveryGroup, candidateGroup));
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

    private PreparedUnit PrepareUnit(RuntimeUnitPlan unit, Task activation, Dictionary<string, IInputUnitFactory> factories, long revision)
    {
        var id = unit.Id;
        if (!id.Kind.StartsWith(RuntimeHost.InputKindPrefix, StringComparison.Ordinal))
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

        var channel = new BudgetedInputChannel(options.InputChannelCapacity, inFlight, message =>
        {
            try
            { currentValues.Capture(id, revision, message); }
            finally { messages.CaptureInput(id, revision, message); }
        }, inputChannels)
        { UsesDurableHandoff = factory.UsesDurableHandoff };
        var reads = new InputReadStatus(segments[0], segments[1], events, deliveries.PendingCounters);
        var group = id;
        try
        {
            // 在单输入的构建失败边界内解析一次；监督者重建复用同一上下文与冻结连接，不读取当前草稿。
            var context = new InputUnitContext(segments[0], segments[1], time, clock, reads, (InputMessageWriter)channel.Writer,
                activation, options.InputBufferBytes > 0 ? inFlight : null, InputModuleCatalog.ReadConnections(unit));
            IRuntimeUnit Build()
            {
                var instance = factory.Build(unit, context);
                if (instance.Id != id)
                { throw new PlanValidationException("输入工厂标识与计划不一致。"); }
                return instance;
            }
            group = factory.RecoveryGroup(unit) ?? throw new PlanValidationException("输入恢复分组不能为空。");
            return new PreparedUnit(id, Build(), null, Build, channel, reads, group);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is not RuntimeUnitBuildException and not PlanValidationException)
            { LogUnexpectedBuildError(logger, id.ToString()); }
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

    internal IRuntimeUnit CreateSender(IOutputTarget target, OutputSenderState state) =>
        new OutputSenderUnit(target, state, deliveries, time, events,
            new RestartBackoff(options.InitialRestartDelay, options.MaximumRestartDelay, options.RestartJitter, _random), controls);

    private static Dictionary<string, IInputUnitFactory> RegisterInputs(IEnumerable<IInputUnitFactory> inputFactories, InputModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(inputFactories);
        var factories = new Dictionary<string, IInputUnitFactory>(StringComparer.Ordinal);
        foreach (var factory in inputFactories.Concat(modules.Factories))
        {
            if (!factory.Kind.StartsWith(RuntimeHost.InputKindPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"输入工厂的种类 {factory.Kind} 应以 {RuntimeHost.InputKindPrefix} 开头。", nameof(inputFactories));
            }

            if (!factories.TryAdd(factory.Kind, factory))
            {
                throw new ArgumentException($"单元种类 {factory.Kind} 注册了多个工厂。", nameof(inputFactories));
            }
        }

        return factories;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "构建运行单元 {UnitId} 时发生未预期的错误。")]
    private static partial void LogUnexpectedBuildError(ILogger logger, string unitId);
}

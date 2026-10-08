using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>只属于一次 Prepare 的 MQTT 工厂对；先构建全部输入，再构建输出，不注册为进程级可变工厂。</summary>
internal sealed class MqttPlanAdapters
{
    private readonly Dictionary<long, Connection> _connections = [];
    private readonly Dictionary<RuntimeUnitId, Input> _inputs = [];
    private readonly Dictionary<(string Recipient, string ClientId), long> _clients = [];
    private readonly ICredentialResolver _credentials;
    private readonly IJsonInputDecoderFactory _decoders;
    private readonly ISharedConnections _shared;
    private readonly IOutboundConnector _connector;
    private readonly TimeProvider _time;
    private readonly string _clientPrefix;
    private readonly Action<long, string> _report;
    private readonly IMqttSubscriptionStore? _subscriptions;

    public MqttPlanAdapters(ICredentialResolver credentials, IJsonInputDecoderFactory decoders, ISharedConnections shared,
        IOutboundConnector connector, TimeProvider time, string gatewayNodeId, Action<long, string> report, IMqttSubscriptionStore? subscriptions = null)
    {
        _credentials = credentials;
        _decoders = decoders;
        _shared = shared;
        _connector = connector;
        _time = time;
        _clientPrefix = "tenon-" + Guid.Parse(gatewayNodeId).ToString("N");
        _report = report;
        _subscriptions = subscriptions;
        InputFactory = new Inputs(this);
        OutputFactory = new MqttOutputFactory(credentials, time, connector, shared, gatewayNodeId)
        { InputGroups = ResolveOutputGroup };
    }

    public IInputUnitFactory InputFactory { get; }
    public IOutputAdapterFactory OutputFactory { get; }

    private MqttConnectionGroup? ResolveOutputGroup(MqttOutputSettings settings)
    {
        var clientId = settings.ClientId ?? $"{_clientPrefix}-{settings.ConnectionId}";
        var group = _connections.TryGetValue(settings.ConnectionId, out var connection) ? connection.Group.Value : null;
        group?.ValidateMember(settings, clientId);
        RegisterClient(settings, clientId);
        return group;
    }

    private void RegisterClient(MqttConnectionSettings settings, string clientId)
    {
        var key = (settings.Recipient, clientId);
        if (_clients.TryGetValue(key, out var connectionId) && connectionId != settings.ConnectionId)
        { throw new ArgumentException("同一 Broker 的实际 ClientId 冲突，请检查默认值与显式配置。"); }
        _clients[key] = settings.ConnectionId;
    }

    private Unit Build(RuntimeUnitPlan plan, InputUnitContext context)
    {
        if (_inputs.TryGetValue(plan.Id, out var existing))
        {
            if (!ReferenceEquals(existing.Context, context))
            { throw new InvalidOperationException("MQTT 计划工厂不能在另一个 Prepare 中复用。"); }
            return new Unit(existing);
        }
        var settings = MqttInputSettings.Parse(plan.Configuration);
        var decoder = _decoders.Build(settings.Fields, settings.UpstreamTime);
        if (settings.UsePassword && _credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient) is null)
        { throw new ArgumentException("MQTT 输入凭证未设置或已失效。"); }
        var clientId = settings.ClientId ?? $"{_clientPrefix}-{settings.ConnectionId}";
        if (!_connections.TryGetValue(settings.ConnectionId, out var connection))
        {
            connection = new Connection(settings, clientId);
            var captured = connection;
            connection.Group = new Lazy<MqttConnectionGroup>(() => new MqttConnectionGroup(settings, clientId,
                captured.Inputs.Select(input => new MqttInputBinding(input.Settings.TopicFilter, input.Settings.Qos,
                    input.ReceiveAsync, input.Settings.ProcessRetained)), _shared, _credentials, _time, _connector,
                captured.WaitForActivationAsync, reason => _report(settings.ConnectionId, reason), _subscriptions, context.BufferBudget));
            _connections.Add(settings.ConnectionId, connection);
        }
        if (connection.Settings.ConnectionIdentity(connection.ClientId) != settings.ConnectionIdentity(clientId))
        { throw new ArgumentException("同一 MQTT 连接的输入参数不一致。"); }
        if (connection.Group.IsValueCreated)
        { throw new InvalidOperationException("MQTT 输出构建后不能继续添加输入绑定。"); }
        RegisterClient(settings, clientId);
        var input = new Input(plan.Id, settings, context, new MqttInputHandoff(context, decoder), connection);
        connection.Inputs.Add(input);
        _inputs.Add(plan.Id, input);
        return new Unit(input);
    }

    private sealed class Inputs(MqttPlanAdapters owner) : IInputUnitFactory
    {
        public string Kind => "input.mqtt";
        public bool UsesDurableHandoff => true;
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            try
            { return owner.Build(unit, context); }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or FormatException)
            { throw new RuntimeUnitBuildException(unit.Id, "MQTT 输入配置无效，请核对连接、订阅、凭证和字段定义。", exception); }
        }
    }

    private sealed class Connection(MqttConnectionSettings settings, string clientId)
    {
        public MqttConnectionSettings Settings { get; } = settings;
        public string ClientId { get; } = clientId;
        public List<Input> Inputs { get; } = [];
        public Lazy<MqttConnectionGroup> Group { get; set; } = null!;
        public async Task WaitForActivationAsync(CancellationToken token)
        {
            // 输入在取得串行租约前报告启动；输出即使先占执行器也不会阻止汇合。
            foreach (var input in Inputs)
            {
                await input.Context.WaitForActivationAsync(token);
                await input.Started.Task.WaitAsync(token);
            }
        }
    }

    private sealed class Input(RuntimeUnitId id, MqttInputSettings settings, InputUnitContext context,
        MqttInputHandoff handoff, Connection connection)
    {
        public RuntimeUnitId Id { get; } = id;
        public MqttInputSettings Settings { get; } = settings;
        public InputUnitContext Context { get; } = context;
        public Connection Connection { get; } = connection;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Receiving;
        public Task<InputHandoffStatus> ReceiveAsync(MQTTnet.MqttApplicationMessage message, CancellationToken token) =>
            Volatile.Read(ref Receiving) == 1 ? handoff.ReceiveAsync(message, token) : Task.FromResult(InputHandoffStatus.Stopped);
    }

    private sealed class Unit(Input input) : IRuntimeUnit
    {
        public RuntimeUnitId Id => input.Id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            Volatile.Write(ref input.Receiving, 1);
            input.Started.TrySetResult();
            IConnectionLease<MqttSession>? lease = null;
            CancellationTokenRegistration quiesce = default;
            try
            {
                await input.Context.WaitForActivationAsync(stoppingToken);
                var group = input.Connection.Group.Value;
                quiesce = stoppingToken.Register(group.BeginQuiesce);
                lease = group.Acquire();
                var retry = new MqttInputRetryDelay();
                while (true)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(input.Settings.TimeoutMs), input.Context.Time);
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stoppingToken);
                    OutputSendResult result;
                    try
                    { result = await lease.ExecuteAsync((session, token) => session.MaintainAsync(token), operation.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    { result = new(OutputSendStatus.Retry); }
                    if (result.Status != OutputSendStatus.Ack)
                    { input.Context.Reads.ReadFailed(input.Context.Time.GetUtcNow(), "MQTT 输入连接不可用，请检查网络、订阅、出站策略或凭证。"); }
                    await Task.Delay(retry.Next(result.Status == OutputSendStatus.Ack), input.Context.Time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally
            {
                try
                {
                    if (lease is not null && stoppingToken.IsCancellationRequested)
                    {
                        using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), input.Context.Time);
                        await input.Connection.Group.Value.DrainAndDisconnectAsync(lease, drainDeadline.Token);
                    }
                }
                finally
                {
                    Volatile.Write(ref input.Receiving, 0);
                    await quiesce.DisposeAsync();
                    if (lease is not null)
                    { await lease.DisposeAsync(); }
                }
            }
        }
        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref input.Receiving, 0);
            input.Started.TrySetResult(); // 未启动实例也不令其他输入永久等待；不允许它接收/确认。
            return ValueTask.CompletedTask;
        }
    }
}

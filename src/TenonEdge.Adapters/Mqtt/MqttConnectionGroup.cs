using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>一个准备计划中某连接的已编译输入集合。构造只做本地校验；全部借用共用一个接收器和会话。
/// 不把准备中的订阅注册进旧计划会话；切换时须先释放旧组的租约，不能并存两套同 ID 连接。</summary>
internal sealed class MqttConnectionGroup
{
    private readonly MqttConnectionSettings _settings;
    private readonly string _clientId;
    private readonly ISharedConnections _connections;
    private readonly ICredentialResolver _credentials;
    private readonly ITlsTrustResolver? _trust;
    private readonly TimeProvider _time;
    private readonly IOutboundConnector _connector;
    private readonly MqttInputReceiver _receiver;
    private readonly Func<CancellationToken, Task> _waitForActivation;
    private readonly string _configurationIdentity;
    private MqttSession? _session;
    private readonly Action<string> _report;
    private int _quiescing;

    public MqttConnectionGroup(MqttConnectionSettings settings, string clientId, IEnumerable<MqttInputBinding> bindings,
        ISharedConnections connections, ICredentialResolver credentials, TimeProvider time, IOutboundConnector connector,
        Func<CancellationToken, Task> waitForActivation, Action<string> report, IMqttSubscriptionStore? subscriptions = null, IInputBufferBudget? bufferBudget = null, ITlsTrustResolver? trust = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(waitForActivation);
        _settings = settings.ValidateConnection();
        _clientId = clientId;
        // 实际 ClientId 同样经过现有长度/空白校验，不能靠调用者绕过连接配置规则。
        _ = (_settings with { ClientId = clientId }).ValidateConnection();
        _connections = connections;
        _credentials = credentials;
        _trust = trust;
        _time = time;
        _connector = connector;
        _waitForActivation = waitForActivation;
        _report = report;
        _receiver = new MqttInputReceiver(bindings, time, report, subscriptions, new(settings.Recipient, clientId), bufferBudget);
        // 计划范围身份只用于本机借用，不改变 Broker ClientId 或持久投递的接收方指纹。
        _configurationIdentity = _settings.ConnectionIdentity(clientId) + ":input-plan:" + Guid.NewGuid().ToString("N");
    }

    internal IConnectionLease<MqttSession> Acquire() => _connections.Acquire(_settings.ConnectionId, "mqtt", _configurationIdentity,
        () =>
        {
            var session = new MqttSession(_settings, _clientId, _credentials, _time, _connector, _receiver, _waitForActivation, _trust);
            Volatile.Write(ref _session, session);
            return session;
        });

    internal void BeginQuiesce()
    {
        _receiver.BeginQuiesce();
        if (Interlocked.Exchange(ref _quiescing, 1) == 0)
        { _report("quiescing"); }
    }

    internal async Task DrainAndDisconnectAsync(IConnectionLease<MqttSession> lease, CancellationToken token)
    {
        BeginQuiesce();
        var session = Volatile.Read(ref _session);
        if (session is not null)
        { await session.CancelOutboundForDrainAsync().WaitAsync(token); }
        await _receiver.QuiesceAsync(token);
        await lease.ExecuteAsync((current, _) =>
        {
            current.DisconnectAfterDrain();
            return Task.FromResult(true);
        }, token);
    }

    internal IOutputAdapter CreateOutputAdapter(MqttConnectionSettings settings, string clientId)
    {
        ValidateMember(settings, clientId);
        return new MqttOutputAdapter(Acquire(), _time, _settings.TimeoutMs);
    }

    /// <summary>准备输出时调用此方法；不等到首次发送才发现同一连接参数冲突。</summary>
    internal void ValidateMember(MqttConnectionSettings settings, string clientId)
    {
        var normalized = settings.ValidateConnection();
        if (normalized.ConnectionId != _settings.ConnectionId || normalized.ConnectionIdentity(clientId) != _settings.ConnectionIdentity(_clientId))
        { throw new ArgumentException("同一 MQTT 输入/输出连接的参数不一致。"); }
    }
}

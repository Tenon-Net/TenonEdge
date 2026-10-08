using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

internal sealed record MqttInputBinding(string Filter, int Qos,
    Func<MqttApplicationMessage, CancellationToken, Task<InputHandoffStatus>> Receive,
    bool ProcessRetained = false);

/// <summary>一个连接的已编译订阅集合；MQTTnet 依次调用回调，绝不把接管等待转为无界后台任务。</summary>
internal sealed class MqttInputReceiver
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumPayloadBytes = 1024 * 1024;
    private readonly MqttInputBinding[] _bindings;
    private readonly Dictionary<string, MqttSubscriptionState> _installed = new(StringComparer.Ordinal);
    private readonly IMqttSubscriptionStore? _subscriptions;
    private readonly MqttSessionIdentity? _session;
    private readonly TimeProvider _time;
    private readonly Action<string> _report;
    private readonly Lock _receiveLock = new();
    private bool _accepting = true;
    private int _activeReceives;
    private TaskCompletionSource? _drained;
    public bool? LastSessionPresent { get; private set; }

    public MqttInputReceiver(IEnumerable<MqttInputBinding> bindings, TimeProvider time, Action<string> report,
        IMqttSubscriptionStore? subscriptions = null, MqttSessionIdentity? session = null, IInputBufferBudget? bufferBudget = null)
    {
        _bindings = bindings.ToArray();
        if (_bindings.Length is < 1 or > 2000)
        { throw new ArgumentException("MQTT 输入绑定数量无效。"); }
        foreach (var binding in _bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(binding.Receive);
            ValidateFilter(binding.Filter);
            if (binding.Qos is not (0 or 1))
            { throw new ArgumentException("MQTT 订阅只支持 QoS 0/1。"); }
        }
        if (subscriptions is not null)
        { ArgumentNullException.ThrowIfNull(session); }
        BufferBudget = bufferBudget;
        _subscriptions = subscriptions;
        _session = session;
        _time = time;
        _report = report;
    }

    internal async Task SubscribeAsync(IMqttClient client, bool sessionPresent, CancellationToken token)
    {
        LastSessionPresent = sessionPresent;
        // 激活后才读取，不在 Prepare 阶段修改旧计划的持久订阅状态。
        if (_subscriptions is not null)
        {
            var saved = _subscriptions.Read(_session!);
            _installed.Clear();
            foreach (var item in saved)
            {
                ValidateFilter(item.Filter);
                if (item.Qos is not (0 or 1))
                { throw new InvalidOperationException("持久订阅状态无效。"); }
                _installed.Add(item.Filter, item);
            }
        }
        if (!sessionPresent)
        {
            if (_installed.Count > 0)
            { _report("sessionLost"); }
            _subscriptions?.Clear(_session!);
            _installed.Clear();
        }
        var desired = _bindings.GroupBy(binding => binding.Filter, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(binding => binding.Qos), StringComparer.Ordinal);
        foreach (var removed in _installed.Keys.Where(filter => !desired.ContainsKey(filter)).ToArray())
        {
            token.ThrowIfCancellationRequested();
            // 即使 SUBACK 未落盘，这个 Filter 也可能已经存在于 Broker，必须退订。
            var result = await client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder()
                .WithTopicFilter(removed).Build(), token);
            if (result.Items.Count != 1 || result.Items.Single().ResultCode is not (MqttClientUnsubscribeResultCode.Success or MqttClientUnsubscribeResultCode.NoSubscriptionExisted))
            { throw new InvalidOperationException("Broker 拒绝 MQTT 输入退订。"); }
            _subscriptions?.Remove(_session!, removed);
            _installed.Remove(removed);
            _report("unsubscribed");
        }
        foreach (var (filter, qos) in desired)
        {
            token.ThrowIfCancellationRequested();
            if (_installed.TryGetValue(filter, out var previous) && previous.Confirmed && previous.Qos == qos)
            { continue; }
            // 先保存意图：网络成功后进程中断也不会留下无法识别、无法退订的孤立 Filter。
            Remember(new(filter, qos, false));
            var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter, (MqttQualityOfServiceLevel)qos).Build(), token);
            if (result.Items.Count != 1 || (int)result.Items.Single().ResultCode > 1)
            { throw new InvalidOperationException("Broker 拒绝 MQTT 输入订阅。"); }
            Remember(new(filter, qos, true));
            _report("subscribed");
        }
    }

    private void Remember(MqttSubscriptionState state)
    {
        _subscriptions?.Put(_session!, state);
        _installed[state.Filter] = state;
    }

    internal IInputBufferBudget? BufferBudget { get; }
    internal void ReportBuffer(string reason) => _report(reason);

    internal void RecordWireOversized() => _report("wireOversized");

    internal bool IsQuiescing { get { lock (_receiveLock) { return !_accepting; } } }
    internal void BeginQuiesce() { lock (_receiveLock) { _accepting = false; } }

    /// <summary>立即停止接收新消息；等待已进入的回调完成接管/确认，调用者取消不重新开放接收。</summary>
    internal Task QuiesceAsync(CancellationToken cancellationToken)
    {
        lock (_receiveLock)
        {
            _accepting = false;
            return _activeReceives == 0 ? Task.CompletedTask : _drained!.Task.WaitAsync(cancellationToken);
        }
    }

    internal async Task ReceiveAsync(MqttApplicationMessageReceivedEventArgs args, Action abort, CancellationToken generation)
    {
        args.AutoAcknowledge = false;
        if (generation.IsCancellationRequested)
        { return; }
        lock (_receiveLock)
        {
            if (!_accepting)
            { return; }
            if (_activeReceives++ == 0)
            { _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        try
        { await ReceiveAcceptedAsync(args, abort, generation); }
        finally
        {
            lock (_receiveLock)
            {
                if (--_activeReceives == 0)
                { _drained!.TrySetResult(); }
            }
        }
    }

    private async Task ReceiveAcceptedAsync(MqttApplicationMessageReceivedEventArgs args, Action abort, CancellationToken generation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(generation, deadline.Token);
        Task<InputHandoffStatus>? handoff = null;
        try
        {
            var message = args.ApplicationMessage;
            if (message.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
            { throw new InvalidOperationException("不支持 QoS 2 输入。"); }
            if (message.Payload.Length > MaximumPayloadBytes)
            { _report("oversized"); }
            else
            {
                var matched = false;
                foreach (var binding in _bindings)
                {
                    if (MqttTopicFilterComparer.Compare(message.Topic, binding.Filter) != MqttTopicFilterCompareResult.IsMatch)
                    { continue; }
                    matched = true;
                    if (message.Retain && !binding.ProcessRetained)
                    { _report("retainedIgnored"); continue; }
                    handoff = binding.Receive(message, operation.Token);
                    var result = await handoff.WaitAsync(operation.Token);
                    handoff = null;
                    if (result is not (InputHandoffStatus.Committed or InputHandoffStatus.Rejected) &&
                        !(message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce && result == InputHandoffStatus.Buffered))
                    { throw new InvalidOperationException("输入未被接管，不能确认。"); }
                }
                if (!matched)
                { _report("unmatched"); }
            }
            operation.Token.ThrowIfCancellationRequested();
            if (message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)
            { await args.AcknowledgeAsync(operation.Token); _report("acknowledged"); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            args.ProcessingFailed = true;
            try
            { if (!generation.IsCancellationRequested) { _report(deadline.IsCancellationRequested ? "handoffTimeout" : "handoffFailed"); } }
            finally { abort(); } // 仅终止本次连接代次；不从回调中重连。
        }
        finally
        {
            // 已中止连接仍不等于实际绑定停止使用原始报文；不确认迟到结果，但保留所有权。
            if (handoff is not null)
            {
                try
                { await handoff; }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    internal static void ValidateFilter(string filter)
    {
        if (string.IsNullOrEmpty(filter) || StrictUtf8.GetByteCount(filter) > 65535 || filter.Contains('\0', StringComparison.Ordinal))
        { throw new ArgumentException("MQTT Topic Filter 无效。"); }
        var levels = filter.Split('/');
        for (var index = 0; index < levels.Length; index++)
        {
            var level = levels[index];
            if (level.Contains('#', StringComparison.Ordinal) && (level != "#" || index != levels.Length - 1) ||
                level.Contains('+', StringComparison.Ordinal) && level != "+")
            { throw new ArgumentException("MQTT Topic Filter 通配符位置无效。"); }
        }
    }
}

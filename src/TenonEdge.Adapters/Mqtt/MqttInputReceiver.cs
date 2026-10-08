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
    private readonly Dictionary<string, int> _installed = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly Action<string> _report;
    private readonly Lock _receiveLock = new();
    private bool _accepting = true;
    private int _activeReceives;
    private TaskCompletionSource? _drained;
    public bool? LastSessionPresent { get; private set; }

    public MqttInputReceiver(IEnumerable<MqttInputBinding> bindings, TimeProvider time, Action<string> report)
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
        _time = time;
        _report = report;
    }

    internal async Task SubscribeAsync(IMqttClient client, bool sessionPresent, CancellationToken token)
    {
        LastSessionPresent = sessionPresent;
        if (!sessionPresent)
        {
            if (_installed.Count > 0)
            { _report("sessionLost"); }
            _installed.Clear();
        }
        foreach (var group in _bindings.GroupBy(binding => binding.Filter, StringComparer.Ordinal))
        {
            var qos = group.Max(binding => binding.Qos);
            if (_installed.TryGetValue(group.Key, out var previous) && previous == qos)
            { continue; }
            var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(group.Key, (MqttQualityOfServiceLevel)qos).Build(), token);
            if (result.Items.Count != 1 || (int)result.Items.Single().ResultCode > 1)
            { throw new InvalidOperationException("Broker 拒绝 MQTT 输入订阅。"); }
            _installed[group.Key] = qos;
            _report("subscribed");
        }
    }

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
                    var result = await binding.Receive(message, operation.Token).WaitAsync(operation.Token);
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

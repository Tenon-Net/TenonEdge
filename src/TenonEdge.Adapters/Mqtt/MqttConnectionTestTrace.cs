using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

public sealed record MqttConnectionTestStep(string Stage, string Status, double? ElapsedMs = null, string? Code = null, string? TimingGroup = null);

/// <summary>仅记录固定五阶段，不接收地址、证书或报文内容；MQTT 3.1.1 的认证/协议结果共享 CONNACK 区间。</summary>
internal sealed class MqttConnectionTestTrace(TimeProvider time, bool tls)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MqttConnectionTestStep> _steps = new(StringComparer.Ordinal)
    {
        ["address"] = new("address", "notReached"),
        ["connection"] = new("connection", "notReached"),
        ["tls"] = new("tls", tls ? "notReached" : "skipped"),
        ["authentication"] = new("authentication", "notReached"),
        ["protocol"] = new("protocol", "notReached"),
    };
    private string? _current;
    private long _started;
    private bool _finished;

    internal void Begin(string stage)
    {
        lock (_lock)
        {
            if (_finished)
            { return; }
            Complete("success", null);
            _current = stage;
            _started = time.GetTimestamp();
        }
    }

    internal void TransportConnected()
    {
        lock (_lock)
        {
            if (_finished)
            { return; }
            Complete("success", null);
            if (_steps["address"].Status == "notReached")
            { _steps["address"] = new("address", "unknown"); }
            if (_steps["connection"].Status == "notReached")
            { _steps["connection"] = new("connection", "success"); } // 非诊断连接器只能证明已取得流，不能捏造耗时。
            if (tls)
            { Begin("tls"); }
        }
    }

    internal MqttConnectionTestStep[] Finish(string code)
    {
        lock (_lock)
        {
            if (!_finished)
            {
                if (_current is null && code != "success")
                {
                    if (_steps["connection"].Status == "success")
                    { _steps["protocol"] = new("protocol", "failed", Code: code); }
                    else
                    {
                        if (_steps["address"].Status == "notReached")
                        { _steps["address"] = new("address", "unknown"); }
                        _steps["connection"] = new("connection", "failed", Code: code);
                    }
                }
                Complete(code == "success" ? "success" : "failed", code);
                var protocol = _steps["protocol"];
                if (protocol.Status != "notReached")
                {
                    _steps["protocol"] = protocol with { TimingGroup = "mqtt.connack" };
                    _steps["authentication"] = new("authentication", code == "success" ? "success"
                        : code == "credential.rejected" ? "failed" : "unknown", protocol.ElapsedMs, code, "mqtt.connack");
                }
                _finished = true;
            }
            return [_steps["address"], _steps["connection"], _steps["tls"], _steps["authentication"], _steps["protocol"]];
        }
    }

    private void Complete(string status, string? code)
    {
        if (_current is null)
        { return; }
        _steps[_current] = new(_current, status, time.GetElapsedTime(_started).TotalMilliseconds, code);
        _current = null;
    }
}

internal sealed class MqttTestConnector(IOutboundConnector inner, MqttConnectionTestTrace trace) : IOutboundConnector
{
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var stream = await inner.ConnectAsync(host, port, stage =>
            trace.Begin(stage == OutboundConnectionStage.Resolving ? "address" : "connection"), cancellationToken);
        trace.TransportConnected();
        return stream;
    }
}

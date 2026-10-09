using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Diagnostics;

public sealed record ConnectionTestStep(string Stage, string Status, double? ElapsedMs = null, string? Code = null, string? TimingGroup = null);

/// <summary>仅记录固定五阶段，不接收地址、证书或报文内容；按协议解释认证结果；HTTP 响应不等于已确认凭证有效。</summary>
internal sealed class ConnectionTestTrace(TimeProvider time, bool tls)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ConnectionTestStep> _steps = new(StringComparer.Ordinal)
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

    internal ConnectionTestStep[] FinishMqtt(string code) => Finish(code, code == "success",
        code == "success" ? "success" : code == "credential.rejected" ? "failed" : "unknown", "mqtt.connack");

    internal ConnectionTestStep[] FinishHttp(string code, int? statusCode, bool credentialUsed) => Finish(code, statusCode is not null,
        statusCode is 401 or 403 ? "failed" : credentialUsed ? "unknown" : "skipped", "http.response", code != "connection.busy");

    internal ConnectionTestStep[] FinishModbus(string code, bool connected, bool reused = false)
    {
        lock (_lock)
        {
            if (!_finished)
            {
                Complete(connected ? "success" : "failed", code);
                if (reused)
                {
                    _steps["address"] = new("address", "skipped");
                    _steps["connection"] = new("connection", "reused", Code: code);
                }
                else if (_steps["connection"].Status == "notReached" && _current is null && code != "connection.backoff")
                {
                    if (_steps["address"].Status == "notReached")
                    { _steps["address"] = new("address", "unknown"); }
                    // 解析已明确失败时，不能声称还尝试过 TCP 建连。
                    if (_steps["address"].Status != "failed")
                    { _steps["connection"] = new("connection", connected ? "success" : "failed", Code: code); }
                }
                _steps["authentication"] = new("authentication", "skipped");
                _steps["protocol"] = new("protocol", "skipped");
                _finished = true;
            }
            return [_steps["address"], _steps["connection"], _steps["tls"], _steps["authentication"], _steps["protocol"]];
        }
    }

    private ConnectionTestStep[] Finish(string code, bool protocolSucceeded, string authenticationStatus, string timingGroup, bool attempted = true)
    {
        lock (_lock)
        {
            if (!_finished)
            {
                if (attempted && _current is null && !protocolSucceeded)
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
                Complete(_current == "protocol" && protocolSucceeded ? "success" : "failed", code);
                var protocol = _steps["protocol"];
                if (protocol.Status != "notReached")
                {
                    _steps["protocol"] = protocol with { TimingGroup = timingGroup };
                    _steps["authentication"] = new("authentication", authenticationStatus,
                        authenticationStatus == "skipped" ? null : protocol.ElapsedMs,
                        authenticationStatus == "skipped" ? null : code,
                        authenticationStatus == "skipped" ? null : timingGroup);
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

internal sealed class ObservedTestConnector(IOutboundConnector inner, ConnectionTestTrace trace) : IOutboundConnector
{
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var stream = await inner.ConnectAsync(host, port, stage =>
            trace.Begin(stage == OutboundConnectionStage.Resolving ? "address" : "connection"), cancellationToken);
        trace.TransportConnected();
        return stream;
    }
}

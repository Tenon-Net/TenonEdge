using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.Host.Features.Connections;

/// <summary>只解析地址，不建连接、不持久化；返回一次规则快照和精确主机授权建议。</summary>
internal sealed class OutboundPolicyInspector
{
    public const int MaximumAddresses = 32;
    private readonly HostOutboundAddressPolicy _policy;
    private readonly TimeProvider _time;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public OutboundPolicyInspector(HostOutboundAddressPolicy policy, TimeProvider time)
        : this(policy, time, (host, token) => Dns.GetHostAddressesAsync(host, token)) { }

    internal OutboundPolicyInspector(HostOutboundAddressPolicy policy, TimeProvider time,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    { _policy = policy; _time = time; _resolve = resolve; }

    public async Task<OutboundInspectionResult> InspectAsync(OutboundInspectionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Length > 253 || request.Host != request.Host.Trim()
            || Uri.CheckHostName(request.Host) == UriHostNameType.Unknown || request.Host.Any(char.IsControl)
            || request.Port is < 1 or > 65535)
        { throw new ArgumentException("请提供有效主机名或IP与1—65535端口，不含协议、路径或凭证。"); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var resolved = IPAddress.TryParse(request.Host, out var literal) ? [literal] : await _resolve(request.Host, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (resolved.Length == 0)
        { throw new SocketException((int)SocketError.HostNotFound); }
        if (resolved.Length > MaximumAddresses)
        { throw new OutboundInspectionLimitException(); }
        var snapshot = _policy.Capture();
        var addresses = resolved.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).Distinct()
            .Select(address =>
            {
                var decision = snapshot.Check(address, request.Port);
                var grant = decision == OutboundAddressDecision.PublicGrantRequired
                    ? new OutboundNetworkGrant($"{address}/{(address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}", request.Port) : null;
                return new OutboundInspectionAddress(address.ToString(), decision, grant);
            }).ToArray();
        return new(request.Host, request.Port, snapshot.Stored, addresses);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OutboundInspectionRequest(string Host, int Port);
internal sealed record OutboundInspectionAddress(string Address, OutboundAddressDecision Decision, OutboundNetworkGrant? Grant);
internal sealed record OutboundInspectionResult(string Host, int Port, OutboundPolicyState Policy, OutboundInspectionAddress[] Addresses);
internal sealed class OutboundInspectionLimitException : Exception;

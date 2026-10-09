using System.Net;
using System.Net.Sockets;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Connections;

/// <summary>不把域名交给 Socket 二次解析，避免检查与连接之间 DNS 变化绕过规则。</summary>
public sealed class OutboundConnector : IOutboundConnector
{
    private readonly IOutboundAddressPolicy _policy;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public OutboundConnector(IOutboundAddressPolicy policy)
        : this(policy, (host, token) => Dns.GetHostAddressesAsync(host, token)) { }

    internal OutboundConnector(IOutboundAddressPolicy policy, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        _policy = policy;
        _resolve = resolve;
    }

    public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
        ConnectCoreAsync(host, port, null, cancellationToken);

    public ValueTask<Stream> ConnectAsync(string host, int port, Action<OutboundConnectionStage> progress, CancellationToken cancellationToken) =>
        ConnectCoreAsync(host, port, progress, cancellationToken);

    private async ValueTask<Stream> ConnectCoreAsync(string host, int port, Action<OutboundConnectionStage>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(OutboundConnectionStage.Resolving);
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _resolve(host, cancellationToken);
        if (addresses.Length == 0)
        { throw new SocketException((int)SocketError.HostNotFound); }
        progress?.Invoke(OutboundConnectionStage.Connecting);
        SocketException? lastFailure = null;
        foreach (var candidate in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            if (!_policy.IsAllowed(address, port))
            { continue; }
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastFailure = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        if (lastFailure is not null)
        { throw lastFailure; }
        throw new OutboundAccessDeniedException();
    }
}

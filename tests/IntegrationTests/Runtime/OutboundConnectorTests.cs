using System.Net;
using System.Net.Sockets;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutboundConnectorTests
{
    [Fact]
    public async Task ConnectUsesTheCheckedAddressAndResolvesAgainOnReconnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolverCalls = 0;
        var policy = new Policy((address, actualPort) => address.Equals(IPAddress.Loopback) && actualPort == port);
        var connector = new OutboundConnector(policy, (_, _) => Task.FromResult<IPAddress[]>(
            ++resolverCalls == 1 ? [IPAddress.Loopback] : [IPAddress.Parse("169.254.169.254")]));
        var token = TestContext.Current.CancellationToken;
        await using (var stream = await connector.ConnectAsync("not-a-real-dns-name.invalid", port, token))
        using (var accepted = await listener.AcceptTcpClientAsync(token))
        {
            await stream.WriteAsync(new byte[] { 42 }, token);
            var read = new byte[1];
            await accepted.GetStream().ReadExactlyAsync(read, token);
            Assert.Equal(42, read[0]);
        }
        await Assert.ThrowsAsync<OutboundAccessDeniedException>(() => connector.ConnectAsync("not-a-real-dns-name.invalid", port, token).AsTask());
        Assert.Equal(2, resolverCalls);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MappedLiteralIsNormalizedAndRevokedPolicyBlocksTheNextConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var allowed = true;
        var policy = new Policy((address, _) =>
        {
            Assert.Equal(IPAddress.Loopback, address);
            return allowed;
        });
        var connector = new OutboundConnector(policy, (_, _) => throw new InvalidOperationException("literal must not use DNS"));
        var token = TestContext.Current.CancellationToken;
        await using (var stream = await connector.ConnectAsync("::ffff:127.0.0.1", port, token))
        using (var accepted = await listener.AcceptTcpClientAsync(token))
        {
            allowed = false;
        }
        await Assert.ThrowsAsync<OutboundAccessDeniedException>(() => connector.ConnectAsync("127.0.0.1", port, token).AsTask());
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task CancellationBeforeResolutionDoesNotStartAnyConnection()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var connector = new OutboundConnector(new Policy((_, _) => throw new InvalidOperationException()),
            (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync("test.invalid", 1883, cancelled.Token).AsTask());
    }

    [Fact]
    public async Task EmptyDnsAnswerIsAResolutionErrorNotAnAccessDenial()
    {
        var connector = new OutboundConnector(new Policy((_, _) => true), (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        var error = await Assert.ThrowsAsync<SocketException>(() => connector.ConnectAsync("test.invalid", 1883, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(SocketError.HostNotFound, error.SocketErrorCode);
    }

    [Fact]
    public async Task ObservedBoundariesDoNotReResolveOrBypassPolicy()
    {
        var stages = new List<OutboundConnectionStage>();
        var resolutions = 0;
        var connector = new OutboundConnector(new Policy((address, _) =>
        {
            Assert.Equal(IPAddress.Loopback, address);
            Assert.Equal(OutboundConnectionStage.Connecting, stages[^1]);
            return false;
        }), (_, _) =>
        {
            Assert.Equal(OutboundConnectionStage.Resolving, Assert.Single(stages));
            resolutions++;
            return Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        });
        await Assert.ThrowsAsync<OutboundAccessDeniedException>(() =>
            connector.ConnectAsync("fixture.invalid", 1883, stages.Add, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, resolutions);
        Assert.Equal([OutboundConnectionStage.Resolving, OutboundConnectionStage.Connecting], stages);
    }

    [Fact]
    public async Task EmptyResolutionDoesNotReportConnectionStage()
    {
        var stages = new List<OutboundConnectionStage>();
        var connector = new OutboundConnector(new Policy((_, _) => throw new InvalidOperationException()),
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        await Assert.ThrowsAsync<SocketException>(() =>
            connector.ConnectAsync("fixture.invalid", 1883, stages.Add, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(OutboundConnectionStage.Resolving, Assert.Single(stages));
    }

    private sealed class Policy(Func<IPAddress, int, bool> allow) : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => allow(address, port);
    }
}

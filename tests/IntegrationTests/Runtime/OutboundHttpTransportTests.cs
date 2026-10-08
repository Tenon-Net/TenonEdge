using System.Net;
using System.Net.Sockets;
using System.Text;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutboundHttpTransportTests
{
    [Fact]
    public async Task OriginalHostIsPreservedAndRedirectIsReturnedWithoutFollowingIt()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var redirect = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        redirect.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var redirectPort = ((IPEndPoint)redirect.LocalEndpoint).Port;
        var resolutions = 0;
        var connector = new OutboundConnector(new LoopbackPolicy(), (_, _) =>
        {
            resolutions++;
            return Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        });
        using var client = OutboundHttpTransport.CreateClient(connector, TimeSpan.FromSeconds(10));
        var request = client.GetAsync(new Uri($"http://original.invalid:{port}/read"), Token);
        using var accepted = await origin.AcceptTcpClientAsync(Token);
        var header = await ReadHeadersAsync(accepted.GetStream());
        Assert.Contains($"Host: original.invalid:{port}\r\n", header, StringComparison.Ordinal);
        await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{redirectPort}/forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), Token);
        using var response = await request;
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, resolutions);
        Assert.False(redirect.Pending());
    }

    [Fact]
    public async Task DeniedAddressNeverReachesTheServer()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var client = OutboundHttpTransport.CreateClient(new OutboundConnector(new DenyPolicy()), TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{port}/"), Token));
        Assert.IsType<OutboundAccessDeniedException>(error.InnerException);
        Assert.False(server.Pending());
    }

    [Fact]
    public void TransportNeverUsesAmbientProxyCookiesOrAutomaticRedirects()
    {
        using var handler = OutboundHttpTransport.CreateHandler(new OutboundConnector(new DenyPolicy()));
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task ExplicitDeadlineBoundsAnUnresponsiveHttpPeer()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var client = OutboundHttpTransport.CreateClient(new OutboundConnector(new LoopbackPolicy()), TimeSpan.FromMilliseconds(500));
        var request = client.GetAsync(new Uri($"http://127.0.0.1:{port}/"), Token);
        using var accepted = await server.AcceptTcpClientAsync(Token);
        await ReadHeadersAsync(accepted.GetStream());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), Token));
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            await stream.ReadExactlyAsync(one, deadline.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }
        throw new InvalidOperationException("测试 HTTP 请求头超限。");
    }

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
    private sealed class DenyPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => false;
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
}

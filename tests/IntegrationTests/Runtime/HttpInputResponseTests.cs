using System.Net;
using System.Net.Sockets;
using System.Text;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class HttpInputResponseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealGetParsesSingleJsonWithKnownOrChunkedLength(bool chunked)
    {
        using var server = Listen();
        var peer = RespondAsync(server, async stream =>
        {
            var body = "{\"value\":9223372036854775807}";
            var length = Encoding.UTF8.GetByteCount(body);
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\n" + (chunked ? "Transfer-Encoding: chunked" : $"Content-Length: {length}") + "\r\nConnection: close\r\n\r\n");
            await WriteAsync(stream, chunked ? $"{length:X}\r\n{body}\r\n0\r\n\r\n" : body);
        });
        using var client = Client();
        using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
        using var document = await HttpInputResponse.ReadAsync(response, Token);
        Assert.Equal(long.MaxValue, document.RootElement.GetProperty("value").GetInt64());
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task OversizedContentLengthClosesWithoutReadingOrDrainingTheBody()
    {
        using var server = Listen();
        var peer = RespondAsync(server, async stream =>
        {
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2000000\r\n\r\n");
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        });
        using var client = Client();
        using (var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token))
        {
            var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(response, Token));
            Assert.Equal(HttpInputFailure.TooLarge, error.Failure);
        }
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task MidBodyDisconnectRemainsANetworkError()
    {
        using var server = Listen();
        var peer = RespondAsync(server, stream => WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{\"value\":"));
        using var client = Client();
        using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
        await Assert.ThrowsAnyAsync<IOException>(() => HttpInputResponse.ReadAsync(response, Token));
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task CancellationAfterHeadersInterruptsStalledBodyAndClosesConnection()
    {
        using var server = Listen();
        var peer = RespondAsync(server, async stream =>
        {
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{");
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        });
        using var client = Client();
        using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var reading = HttpInputResponse.ReadAsync(response, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static HttpClient Client() => OutboundHttpTransport.CreateClient(new OutboundConnector(new LoopbackPolicy()), TimeSpan.FromSeconds(5));
    private sealed class LoopbackPolicy : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static Uri Address(TcpListener listener) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/values");
    private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), Token).AsTask();
    private static async Task RespondAsync(TcpListener listener, Func<NetworkStream, Task> respond)
    {
        using var socket = await listener.AcceptTcpClientAsync(Token);
        var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("GET /values HTTP/1.1", await reader.ReadLineAsync(Token));
        while (await reader.ReadLineAsync(Token) is { Length: > 0 })
        { }
        await respond(stream);
    }
}

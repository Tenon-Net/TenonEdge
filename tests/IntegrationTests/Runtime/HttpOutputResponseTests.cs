using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class HttpOutputResponseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(200, "{\"code\":0}", OutputSendStatus.Ack, false)]
    [InlineData(200, "{\"code\":42}", OutputSendStatus.Retry, true)]
    [InlineData(200, "private-invalid-body", OutputSendStatus.Retry, true)]
    [InlineData(302, "", OutputSendStatus.SuspendTarget, false)]
    [InlineData(409, "", OutputSendStatus.RejectMessage, false)]
    [InlineData(500, "", OutputSendStatus.Retry, true)]
    [InlineData(502, "", OutputSendStatus.Retry, false)]
    [InlineData(503, "", OutputSendStatus.Retry, false)]
    public async Task ActualHttpStatusAndBusinessBodiesUseFourResultCategories(int status, string body, OutputSendStatus expected, bool poison)
    {
        using var server = Listen();
        var bytes = Encoding.UTF8.GetBytes(body);
        var peer = RespondAsync(server, async stream =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), Token);
            await stream.WriteAsync(bytes, Token);
        });
        using var client = Client();
        using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
        var result = await Evaluator().EvaluateAsync(response, DateTimeOffset.UtcNow, Token);
        Assert.Equal(expected, result.Status);
        Assert.Equal(poison, result.CountsTowardPoison);
        Assert.DoesNotContain("private-invalid-body", result.Error ?? "", StringComparison.Ordinal);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task BodyConnectionLossIsRetryWithoutPoisonEvenAfterHeadersArrived()
    {
        using var server = Listen();
        var peer = RespondAsync(server, stream => stream.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{\"code\":"), Token).AsTask());
        using var client = Client();
        using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
        var result = await Evaluator().EvaluateAsync(response, DateTimeOffset.UtcNow, Token);
        Assert.Equal(OutputSendStatus.Retry, result.Status);
        Assert.False(result.CountsTowardPoison);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task HeaderOnlyOverLimitResponseIsRejectedWithoutWaitingForOrDrainingBody()
    {
        using var server = Listen();
        var peer = RespondAsync(server, async stream =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\n\r\n"), Token);
            var eof = await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(0, eof);
        });
        using var client = Client();
        using (var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token))
        {
            var result = await Evaluator().EvaluateAsync(response, DateTimeOffset.UtcNow, Token);
            Assert.Equal(OutputSendStatus.Retry, result.Status);
            Assert.True(result.CountsTowardPoison);
        }
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task UnknownLengthBodyIsBoundedAndNeverTreatsTruncatedJsonAsSuccess()
    {
        using var server = Listen();
        var peer = RespondAsync(server, async stream =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"), Token);
            // 无 Content-Length；合法 JSON 超出上限，不能只检查开头的成功字段。
            var body = "{\"code\":0,\"padding\":\"" + new string('x', HttpOutputResponse.MaximumBodyBytes) + "\"}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), Token);
        });
        using var client = Client();
        using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
        var result = await Evaluator().EvaluateAsync(response, DateTimeOffset.UtcNow, Token);
        Assert.Equal(OutputSendStatus.Retry, result.Status);
        Assert.True(result.CountsTowardPoison);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task CallerDeadlineAlsoCancelsStalledBodyAfterHeaders()
    {
        using var server = Listen();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = RespondAsync(server, async stream =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n"), Token);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        });
        try
        {
            using var client = Client();
            using var response = await client.GetAsync(Address(server), HttpCompletionOption.ResponseHeadersRead, Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Evaluator().EvaluateAsync(response, DateTimeOffset.UtcNow, deadline.Token));
        }
        finally { release.TrySetResult(); await peer; }
    }

    private static HttpOutputResponse Evaluator() => new(new HttpSuccessRule("/code", JsonSerializer.SerializeToElement(0)));
    private static HttpClient Client() => OutboundHttpTransport.CreateClient(new OutboundConnector(new LoopbackPolicy()), TimeSpan.FromSeconds(5));
    private static Uri Address(TcpListener server) => new($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/");
    private static TcpListener Listen()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        return server;
    }
    private static async Task RespondAsync(TcpListener server, Func<NetworkStream, Task> respond)
    {
        using var socket = await server.AcceptTcpClientAsync(Token);
        var stream = socket.GetStream();
        var end = 0;
        var one = new byte[1];
        for (var count = 0; count < 8192; count++)
        {
            await stream.ReadExactlyAsync(one, Token);
            end = ((end << 8) | one[0]);
            if (end == 0x0d0a0d0a)
            { await respond(stream); return; }
        }
        throw new InvalidOperationException("测试请求头未结束。");
    }
    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class HttpConnectionTestTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(200, true, "skipped")]
    [InlineData(302, false, "skipped")]
    [InlineData(401, false, "failed")]
    [InlineData(403, false, "failed")]
    [InlineData(500, false, "skipped")]
    public async Task ReadsOnlyStatusDoesNotFollowRedirectOrWaitForBody(int status, bool success, string authentication)
    {
        using var server = StartServer();
        using var redirect = StartServer();
        var settings = Settings(server);
        var probe = new HttpConnectionTest(new SharedConnections(), Connector(), TimeProvider.System);
        var operation = probe.RunAsync(settings, null, Token);
        using var peer = await server.AcceptTcpClientAsync(Token);
        var headers = await ReadHeadersAsync(peer.GetStream());
        Assert.StartsWith("GET /base/ HTTP/1.1\r\n", headers, StringComparison.Ordinal);
        var target = $"http://127.0.0.1:{((IPEndPoint)redirect.LocalEndpoint).Port}/";
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} fixture\r\nLocation: {target}\r\nContent-Length: 10000000\r\n\r\n"), Token);
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(success, result.Success);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(status is 401 or 403 ? "credential.rejected" : "http.response", result.Code);
        Assert.Equal("success", result.Steps[0].Status);
        Assert.Equal("success", result.Steps[1].Status);
        Assert.Equal("skipped", result.Steps[2].Status);
        Assert.Equal(authentication, result.Steps[3].Status);
        Assert.Equal("success", result.Steps[4].Status);
        Assert.False(redirect.Pending());
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], Token));
    }

    [Theory]
    [InlineData(HttpCredentialKind.Basic, "user", null, "secret", "Authorization: Basic dXNlcjpzZWNyZXQ=")]
    [InlineData(HttpCredentialKind.Bearer, null, null, "fixture-secret", "Authorization: Bearer fixture-secret")]
    [InlineData(HttpCredentialKind.ApiKeyHeader, null, "X-Test-Key", "fixture-secret", "X-Test-Key: fixture-secret")]
    [InlineData(HttpCredentialKind.ApiKeyQuery, null, "key", "a&b", "GET /base/?key=a%26b HTTP/1.1")]
    public async Task UsesExistingCredentialEncodingWithoutReturningAnySecret(HttpCredentialKind kind, string? user, string? name, string secret, string wire)
    {
        using var server = StartServer();
        var probe = new HttpConnectionTest(new SharedConnections(), Connector(), TimeProvider.System);
        var operation = probe.RunAsync(Settings(server), new(new(kind, user, name, "fixture", "1"), secret), Token);
        using var peer = await server.AcceptTcpClientAsync(Token);
        Assert.Contains(wire, await ReadHeadersAsync(peer.GetStream()), StringComparison.Ordinal);
        await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), Token);
        var result = await operation;
        Assert.True(result.Success);
        Assert.Equal("unknown", result.Steps[3].Status); // HTTP 200 本身不能证明服务器验证了秘密。
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedSerialLeaseWaitIsBoundedAndDoesNotOpenAnotherClient()
    {
        using var server = StartServer();
        var settings = Settings(server) with { TimeoutMs = 100 };
        var shared = new SharedConnections();
        var connector = Connector();
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        await using var runtimeLease = shared.Acquire(1, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = runtimeLease.ExecuteAsync(async (_, _) => { entered.SetResult(); await finish.Task; return true; }, Token);
        await entered.Task;
        try
        {
            var result = await new HttpConnectionTest(shared, connector, TimeProvider.System).RunAsync(settings, null, Token);
            Assert.Equal("connection.busy", result.Code);
            Assert.Equal("notReached", result.Steps[0].Status);
            Assert.Equal("notReached", result.Steps[1].Status);
            Assert.False(server.Pending());
        }
        finally { finish.TrySetResult(); await held; }
        var changed = settings with { TimeoutMs = 200 };
        await Assert.ThrowsAsync<HttpConnectionUnavailableException>(() => new HttpConnectionTest(shared, connector, TimeProvider.System).RunAsync(changed, null, Token));
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task TestBorrowsExistingRuntimeSessionAndNormalReadsWorkAfterIt()
    {
        using var server = StartServer();
        var settings = Settings(server);
        var shared = new SharedConnections();
        var connector = Connector();
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        await using var runtimeLease = shared.Acquire(1, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs));
        var input = new HttpInputSettings { ConnectionId = 1, BaseAddress = settings.BaseAddress };
        var before = runtimeLease.ExecuteAsync((session, token) => session.ReadInputAsync(input, new NoCredentials(), token), Token);
        using var first = await server.AcceptTcpClientAsync(Token);
        _ = await ReadHeadersAsync(first.GetStream());
        await first.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 7\r\n\r\n{\"x\":1}"u8.ToArray(), Token);
        using (var data = await before)
        { Assert.Equal(1, data.RootElement.GetProperty("x").GetInt32()); }
        // 如果错误地另建会话，工厂中的 ForbiddenConnector 会立刻使测试失败。
        var testing = new HttpConnectionTest(shared, new ForbiddenConnector(), TimeProvider.System).RunAsync(settings, null, Token);
        Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], Token));
        using var second = await server.AcceptTcpClientAsync(Token);
        _ = await ReadHeadersAsync(second.GetStream());
        await second.GetStream().WriteAsync("HTTP/1.1 204 No Content\r\n\r\n"u8.ToArray(), Token);
        Assert.True((await testing).Success);
        var after = runtimeLease.ExecuteAsync((session, token) => session.ReadInputAsync(input, new NoCredentials(), token), Token);
        using var third = await server.AcceptTcpClientAsync(Token);
        _ = await ReadHeadersAsync(third.GetStream());
        await third.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 7\r\n\r\n{\"x\":2}"u8.ToArray(), Token);
        using var updated = await after;
        Assert.Equal(2, updated.RootElement.GetProperty("x").GetInt32());
    }

    private sealed class ForbiddenConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("test must borrow the existing session");
    }
    private sealed class NoCredentials : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }

    [Fact]
    public async Task PolicyTimeoutAndCallerCancellationKeepTheirMeaning()
    {
        using var server = StartServer();
        var denied = new HttpConnectionTest(new SharedConnections(), new OutboundConnector(new Policy(false)), TimeProvider.System);
        Assert.Equal("outbound.denied", (await denied.RunAsync(Settings(server), null, Token)).Code);
        Assert.False(server.Pending());
        var probe = new HttpConnectionTest(new SharedConnections(), Connector(), TimeProvider.System);
        var timeout = probe.RunAsync(Settings(server) with { TimeoutMs = 200 }, null, Token);
        using (var peer = await server.AcceptTcpClientAsync(Token))
        {
            _ = await ReadHeadersAsync(peer.GetStream());
            Assert.Equal("timeout", (await timeout).Code);
            Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], Token));
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var canceled = probe.RunAsync(Settings(server), null, stop.Token);
        using var accepted = await server.AcceptTcpClientAsync(Token);
        _ = await ReadHeadersAsync(accepted.GetStream());
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(0, await accepted.GetStream().ReadAsync(new byte[1], Token));
    }

    private static TcpListener StartServer()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        return server;
    }

    private static HttpRequestSettings Settings(TcpListener server) => new()
    { ConnectionId = 1, BaseAddress = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/base/" };
    private static OutboundConnector Connector() => new(new Policy(true));
    private sealed class Policy(bool allowed) : IOutboundAddressPolicy
    { public bool IsAllowed(IPAddress address, int port) => allowed; }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            await stream.ReadExactlyAsync(one, timeout.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            { return Encoding.ASCII.GetString(bytes.ToArray()); }
        }
        throw new InvalidOperationException("fixture request headers exceeded limit");
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class HttpConnectionObservationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(401, "{}", "http.status")]
    [InlineData(503, "{}", "http.status")]
    [InlineData(200, "fixture-secret", "payload.invalidJson")]
    public async Task RealReadFailureAndRecoveryShareOneObservationWithoutLeakingResponse(int status, string body, string code)
    {
        using var listener = Listen();
        var time = new FakeTimeProvider();
        var settings = Input(listener);
        await using var session = new HttpConnectionSession(Connector(), 5000, time: time);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), session.Observation);
        var first = ReplyAsync(listener, 200, "{}");
        using (await session.ReadInputAsync(settings, new Credentials(), Token))
        { }
        await first;
        var success = session.Observation;
        Assert.Equal(time.GetUtcNow(), success.LastSuccessAt);
        time.Advance(TimeSpan.FromSeconds(1));
        var failing = ReplyAsync(listener, status, body);
        await Assert.ThrowsAsync<HttpInputResponseException>(() => session.ReadDiagnosticInputAsync(settings, new Credentials(), Token));
        await failing;
        var failed = session.Observation;
        Assert.Equal(ConnectionCommunicationState.Degraded, failed.State);
        Assert.Equal(code, failed.LastErrorCode);
        Assert.Equal(time.GetUtcNow(), failed.LastErrorAt);
        Assert.Equal(success.LastSuccessAt, failed.LastSuccessAt);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
        // 原共享连接上的手工预览成功，不覆盖业务读取的失败；预览仍真实联网。
        var preview = ReplyAsync(listener, 200, "{}");
        Assert.True((await session.ReadPreviewAsync(settings, new Credentials(), null,
            ClockSyncState.Unknown, time, 65536, Token)).Success);
        await preview;
        Assert.Equal(failed, session.Observation);
        time.Advance(TimeSpan.FromSeconds(1));
        var output = new HttpOutputSettings { ConnectionId = 1, BaseAddress = settings.BaseAddress };
        var recovery = ReplyAsync(listener, 204, "");
        var message = output.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), null);
        Assert.Equal(OutputSendStatus.Ack, (await session.SendOutputAsync(output, new Credentials(), time, message, Token)).Status);
        await recovery;
        Assert.Equal(ConnectionCommunicationState.Connected, session.Observation.State);
        Assert.Equal(time.GetUtcNow(), session.Observation.LastSuccessAt);
        Assert.Equal(failed.LastErrorAt, session.Observation.LastErrorAt);
        Assert.Equal(failed.LastErrorCode, session.Observation.LastErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputResponseFailureUsesSafeFixedCodeAndNoProtectedBody(bool businessRule)
    {
        using var listener = Listen();
        var time = new FakeTimeProvider();
        await using var session = new HttpConnectionSession(Connector(), 5000, time: time);
        var settings = new HttpOutputSettings
        {
            ConnectionId = 1,
            BaseAddress = Input(listener).BaseAddress,
            SuccessPath = businessRule ? "/ok" : null,
            SuccessValue = businessRule ? JsonSerializer.SerializeToElement(true) : default
        };
        var peer = ReplyAsync(listener, businessRule ? 200 : 503, "{\"ok\":false,\"password\":\"fixture-secret\"}");
        var message = settings.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), null);
        Assert.Equal(OutputSendStatus.Retry, (await session.SendOutputAsync(settings, new Credentials(), time, message, Token)).Status);
        await peer;
        Assert.Equal(ConnectionCommunicationState.Degraded, session.Observation.State);
        Assert.Equal(businessRule ? "http.successRuleFailed" : "http.statusRejected", session.Observation.LastErrorCode);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(session.Observation), StringComparison.Ordinal);
        Assert.Null(session.Observation.LastSuccessAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InFlightTimeoutIsDistinctFromStoppingAndQueuedTimeoutIsNotAnIoFailure(bool output, bool timeout)
    {
        using var listener = Listen();
        var time = new FakeTimeProvider();
        var input = Input(listener);
        var settings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = input.BaseAddress, TimeoutMs = 5000 };
        var shared = new SharedConnections();
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        await using var lease = shared.Acquire(1, "http", identity, () => new HttpConnectionSession(Connector(), 5000, time: time));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var message = settings.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), null);
        var operation = output
            ? lease.ExecuteAsync(async (session, token) => { await session.SendOutputAsync(settings, new Credentials(), time, message, token, stop.Token); return true; }, deadline.Token)
            : lease.ExecuteAsync(async (session, token) => { using var document = (await session.ReadDiagnosticInputAsync(input, new Credentials(), token, stop.Token)).Document; return true; }, deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await ReadRequestAsync(peer.GetStream());
        // 同一串行器上的等待超时未进入会话，不能改连接观测或创建第二客户端。
        await using var adapter = new HttpOutputAdapter(shared.Acquire<HttpConnectionSession>(1, "http", identity, () => throw new InvalidOperationException()), settings, new Credentials(), time);
        var queued = adapter.SendAsync(message, Token);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(OutputSendStatus.Retry, (await queued.WaitAsync(TimeSpan.FromSeconds(5), Token)).Status);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), shared.Observe(1, "http", identity)!.Observation);
        Assert.False(listener.Pending());
        if (timeout)
        { await deadline.CancelAsync(); }
        else
        { await stop.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var observation = shared.Observe(1, "http", identity)!.Observation!;
        Assert.Equal(timeout ? ConnectionCommunicationState.Degraded : ConnectionCommunicationState.Connecting, observation.State);
        Assert.Equal(timeout ? "http.timeout" : null, observation.LastErrorCode);
        Assert.Equal(timeout ? time.GetUtcNow() : (DateTimeOffset?)null, observation.LastErrorAt);
    }

    [Fact]
    public async Task ExplicitConnectionTestDoesNotCreateBusinessSuccessAndLocalCredentialFailureIsNotNetworkFailure()
    {
        using var listener = Listen();
        var time = new FakeTimeProvider();
        var settings = Input(listener);
        await using var session = new HttpConnectionSession(Connector(), 5000, time: time);
        var peer = ReplyAsync(listener, 200, "");
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUri);
        Assert.True((await session.TestConnectionAsync(settings, request, null, time, Token)).Success);
        await peer;
        Assert.Equal(new(ConnectionCommunicationState.Connecting), session.Observation);
        await Assert.ThrowsAsync<HttpCredentialUnavailableException>(() => session.ReadInputAsync(settings, new Credentials(true), Token));
        Assert.Equal("http.credentialsUnavailable", session.Observation.LastErrorCode);
        Assert.False(listener.Pending());
    }

    private static HttpInputSettings Input(TcpListener listener) => new()
    { ConnectionId = 1, BaseAddress = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/" };
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static OutboundConnector Connector() => new(new ConnectionTrustTests.LoopbackPolicy());
    private sealed class Credentials(bool unavailable = false) : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => unavailable ? new(HttpCredentialKind.Bearer, null, null, "fixture", "v1") : null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private static async Task ReplyAsync(TcpListener listener, int status, string body)
    {
        using var peer = await listener.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        var stream = peer.GetStream();
        await ReadRequestAsync(stream);
        var response = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} fixture\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        await stream.WriteAsync(response, Token);
    }
    private static async Task ReadRequestAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        do
        {
            await stream.ReadExactlyAsync(one, Token);
            bytes.Add(one[0]);
            Assert.True(bytes.Count <= 32768);
        } while (bytes.Count < 4 || !bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()));
        var headers = Encoding.ASCII.GetString(bytes.ToArray());
        var length = headers.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (length is not null)
        {
            var count = int.Parse(length.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(count, 0, 1024);
            await stream.ReadExactlyAsync(new byte[count], Token);
        }
    }
}

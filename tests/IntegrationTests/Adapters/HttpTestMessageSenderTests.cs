using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class HttpTestMessageSenderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("envelope")]
    [InlineData("values")]
    [InlineData("custom")]
    public async Task OnePostUsesTheExactProductionPayloadHeadersAndIdWithoutTestMarkersOrRetries(string mode)
    {
        using var server = Listen();
        var settings = Settings(server) with
        {
            Payload = JsonSerializer.Deserialize<JsonElement>(mode == "envelope" ? "null" : mode == "values" ?
            """{"mode":"values"}""" : """{"mode":"custom","fields":[{"target":"value","sourceField":"value"},{"target":"id","placeholder":"{messageId}"}]}""")
        };
        var connector = new Connector();
        var shared = new SharedConnections();
        var target = Target(settings, shared, connector);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var source = new EdgeMessage(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(long.MaxValue) });
        var message = target.CreateMessage(source, id);
        var sending = new HttpTestMessageSender(shared, connector, TimeProvider.System).SendAsync(settings, null, message, Token);
        using var peer = await Accept(server);
        var wire = await ReadRequest(peer.GetStream());
        Assert.StartsWith("POST /api/events HTTP/1.1\r\n", wire.Headers, StringComparison.Ordinal);
        Assert.Contains($"Idempotency-Key: {id:D}\r\n", wire.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Static: frozen\r\n", wire.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type: application/json\r\n", wire.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(message.Payload.ToArray(), wire.Body);
        Assert.Contains("9223372036854775807", Encoding.UTF8.GetString(wire.Body), StringComparison.Ordinal);
        await Reply(peer.GetStream(), 503, "{\"message\":\"not available\"}");
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("http.response", result.Code);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("{\"message\":\"not available\"}", result.Body);
        Assert.Equal(1, connector.Calls);
        Assert.False(server.Pending());
        await AssertClosed(peer.GetStream());
    }

    [Theory]
    [InlineData(HttpCredentialKind.Basic)]
    [InlineData(HttpCredentialKind.Bearer)]
    [InlineData(HttpCredentialKind.ApiKeyHeader)]
    [InlineData(HttpCredentialKind.ApiKeyQuery)]
    public async Task UsesTheDetachedCredentialSnapshotAndHidesItsEcho(HttpCredentialKind kind)
    {
        using var server = Listen();
        var settings = Settings(server);
        var credential = new HttpCredential(new(kind, kind == HttpCredentialKind.Basic ? "fixture-user" : null,
            kind is HttpCredentialKind.ApiKeyHeader or HttpCredentialKind.ApiKeyQuery ? "key" : null, "account", "v1"), "fixture-secret");
        var connector = new Connector();
        var sending = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System)
            .SendAsync(settings, credential, Message(settings, credential), Token);
        using var peer = await Accept(server);
        var wire = await ReadRequest(peer.GetStream());
        var echo = kind == HttpCredentialKind.Basic ? Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-secret")) : credential.Secret;
        Assert.Contains(kind switch
        {
            HttpCredentialKind.Basic => "Authorization: Basic " + echo,
            HttpCredentialKind.Bearer => "Authorization: Bearer " + echo,
            HttpCredentialKind.ApiKeyHeader => "key: " + echo,
            _ => "POST /api/events?key=" + echo + " HTTP/1.1",
        }, wire.Headers, StringComparison.Ordinal);
        await Reply(peer.GetStream(), 200, JsonSerializer.Serialize(new { message = echo }));
        var result = await sending;
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(HttpResponseSummaryState.Redacted, result.BodyState);
        Assert.Null(result.Body);
        Assert.DoesNotContain(echo, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task BorrowsTheOriginalSerialSessionAndNormalSendingContinuesOnThatSocket()
    {
        using var server = Listen();
        var shared = new SharedConnections();
        var connector = new Connector();
        var settings = Settings(server);
        await using var adapter = Target(settings, shared, connector).CreateAdapter();
        var message = Message(settings);
        var first = adapter.SendAsync(message, Token);
        using var peer = await Accept(server);
        var original = await ReadRequest(peer.GetStream());
        // 原请求未回响应；测试请求只能等原串行租约，不能另建连接。
        var forbidden = new Connector { Deny = true };
        var testing = new HttpTestMessageSender(shared, forbidden, TimeProvider.System).SendAsync(settings, null, message, Token);
        Assert.False(testing.IsCompleted);
        Assert.Equal(0, peer.Available);
        Assert.False(server.Pending());
        await Reply(peer.GetStream(), 200, "", keepAlive: true);
        Assert.Equal(OutputSendStatus.Ack, (await first).Status);
        var tested = await ReadRequest(peer.GetStream());
        Assert.Equal(original, tested, RequestComparer.Instance);
        await Reply(peer.GetStream(), 200, "{\"value\":9223372036854775807}", keepAlive: true);
        Assert.Equal(HttpResponseSummaryState.Captured, (await testing).BodyState);
        var after = adapter.SendAsync(message, Token);
        Assert.Equal(original, await ReadRequest(peer.GetStream()), RequestComparer.Instance);
        await Reply(peer.GetStream(), 200, "");
        Assert.Equal(OutputSendStatus.Ack, (await after).Status);
        Assert.Equal(1, connector.Calls);
        Assert.Equal(0, forbidden.Calls);
    }

    [Fact]
    public async Task WaitingForTheExistingLeaseIsBoundedAndChangedConnectionIdentityIsRefused()
    {
        using var server = Listen();
        var clock = new FakeTimeProvider();
        var shared = new SharedConnections();
        var connector = new Connector();
        var settings = Settings(server);
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        await using var lease = shared.Acquire(1, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = lease.ExecuteAsync(async (_, _) => { entered.SetResult(); await finish.Task; return true; }, Token);
        await entered.Task;
        try
        {
            var sending = new HttpTestMessageSender(shared, connector, clock).SendAsync(settings, null, Message(settings), Token);
            clock.Advance(TimeSpan.FromMilliseconds(settings.TimeoutMs));
            Assert.Equal("connection.busy", (await sending.WaitAsync(TimeSpan.FromSeconds(5), Token)).Code);
            Assert.Equal(0, connector.Calls);
            var changed = settings with { TimeoutMs = settings.TimeoutMs + 1 };
            await Assert.ThrowsAsync<HttpConnectionUnavailableException>(() => new HttpTestMessageSender(shared, connector, clock)
                .SendAsync(changed, null, Message(changed), Token));
            Assert.False(server.Pending());
        }
        finally { finish.TrySetResult(); await holding; }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    [InlineData("bodyTimeout")]
    [InlineData("bodyCancel")]
    public async Task LostResponseAndCancellationNeverReplayOrClaimThatThePostWasUndone(string scenario)
    {
        using var server = Listen();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var clock = new FakeTimeProvider();
        var connector = new Connector();
        var settings = Settings(server);
        var sending = new HttpTestMessageSender(new SharedConnections(), connector, clock).SendAsync(settings, null, Message(settings), stop.Token);
        using var peer = await Accept(server);
        var stream = peer.GetStream();
        _ = await ReadRequest(stream);
        if (scenario.StartsWith("body", StringComparison.Ordinal))
        {
            // 等待服务端响应流首次读正文，避免仅写出响应头就推进时钟的竞态。
            await stream.WriteAsync("HTTP/1.1 429 Limited\r\nContent-Length: 100\r\n\r\nx"u8.ToArray(), Token);
            await connector.BodyRead.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        if (scenario.EndsWith("cancel", StringComparison.OrdinalIgnoreCase))
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
        else
        {
            if (scenario == "disconnect")
            { peer.Dispose(); }
            else
            { clock.Advance(TimeSpan.FromMilliseconds(settings.TimeoutMs)); }
            var result = await sending.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(scenario == "bodyTimeout" ? "http.response" : scenario == "timeout" ? "http.timeoutUnknown" : "http.deliveryUnknown", result.Code);
            Assert.Equal(scenario == "bodyTimeout" ? 429 : (int?)null, result.StatusCode);
            Assert.Equal(scenario == "bodyTimeout" ? HttpResponseSummaryState.Unavailable : (HttpResponseSummaryState?)null, result.BodyState);
            Assert.Null(result.Body);
        }
        if (scenario != "disconnect")
        { await AssertClosed(stream); }
        Assert.Equal(1, connector.Calls);
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task RedirectLargeUnfinishedResponseAndCookieDoNotTriggerAnotherRequestOrBodyDrain()
    {
        using var server = Listen();
        using var redirect = Listen();
        var settings = Settings(server);
        var connector = new Connector();
        var sending = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System).SendAsync(settings, null, Message(settings), Token);
        using var peer = await Accept(server);
        _ = await ReadRequest(peer.GetStream());
        var target = Settings(redirect).BaseAddress;
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {target}\r\nSet-Cookie: private=value\r\nContent-Length: 99999999\r\n\r\n"), Token);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(302, result.StatusCode);
        Assert.Equal(HttpResponseSummaryState.TooLarge, result.BodyState);
        Assert.True(result.Truncated);
        Assert.Null(result.Body);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.False(redirect.Pending());
        Assert.Equal(1, connector.Calls);
        await AssertClosed(peer.GetStream());
    }

    [Fact]
    public async Task UnknownLengthChunkedBodyStopsAtTheLimitWithoutWaitingForTheRestOfTheChunk()
    {
        using var server = Listen();
        var settings = Settings(server);
        var connector = new Connector();
        var sending = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System).SendAsync(settings, null, Message(settings), Token);
        using var peer = await Accept(server);
        _ = await ReadRequest(peer.GetStream());
        await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n100000\r\n"u8.ToArray(), Token);
        await peer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"message\":\"" + new string('x', HttpTestResponseCapture.MaximumBodyBytes - 12)), Token);
        // 不发送本块剩余字节、CRLF或零块。接收方必须立即结束，不尝试排空或解析残缺JSON。
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(HttpResponseSummaryState.TooLarge, result.BodyState);
        Assert.True(result.Truncated);
        Assert.Null(result.Body);
        Assert.Equal(1, connector.Calls);
        await AssertClosed(peer.GetStream());
    }

    [Fact]
    public async Task RecipientChangeAndDeniedDestinationNeverSend()
    {
        using var server = Listen();
        var connector = new Connector { Deny = true };
        var settings = Settings(server);
        var sender = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System);
        var changed = await sender.SendAsync(settings, null, Message(settings) with { Fingerprint = "changed" }, Token);
        Assert.Equal("recipient.changed", changed.Code);
        Assert.Equal(0, connector.Calls);
        Assert.Equal("outbound.denied", (await sender.SendAsync(settings, null, Message(settings), Token)).Code);
        Assert.Equal(1, connector.Calls);
        Assert.False(server.Pending());
    }

    [Theory]
    [InlineData("path")]
    [InlineData("metadata")]
    [InlineData("qos")]
    [InlineData("empty")]
    [InlineData("id")]
    [InlineData("header")]
    [InlineData("credential")]
    public async Task InvalidRequestsAreRejectedBeforeAnyConnection(string scenario)
    {
        using var server = Listen();
        var settings = Settings(server);
        var connector = new Connector();
        var message = Message(settings);
        message = scenario switch
        {
            "path" => message with { Address = "https://forbidden.invalid/" },
            "metadata" => message with { ProtocolMetadata = null },
            "qos" => message with { QualityOfService = 1 },
            "empty" => message with { Payload = ReadOnlyMemory<byte>.Empty },
            "id" => message with { Headers = ImmutableDictionary<string, string>.Empty },
            "header" => message with { Headers = message.Headers!.ToImmutableDictionary().Add("Authorization", "secret") },
            _ => message,
        };
        var credential = scenario == "credential" ? new HttpCredential(new(HttpCredentialKind.Bearer, null, null, "account", "v1"), "bad\r\ncredential") : null;
        await Assert.ThrowsAsync<ArgumentException>(() => new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System)
            .SendAsync(settings, credential, message, Token));
        Assert.Equal(0, connector.Calls);
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task HttpsStillRejectsAnUntrustedServerBeforePostingCredentialsOrPayload()
    {
        using var server = Listen();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var settings = Settings(server) with { BaseAddress = Settings(server).BaseAddress.Replace("http:", "https:", StringComparison.Ordinal) };
        var connector = new Connector();
        var sending = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System).SendAsync(settings, null, Message(settings), Token);
        using var peer = await Accept(server);
        await using var tls = new SslStream(peer.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, Token);
            Assert.Equal(0, await tls.ReadAsync(new byte[1], Token));
        }
        catch (Exception error) when (error is AuthenticationException or IOException) { }
        Assert.Equal("certificate.invalid", (await sending.WaitAsync(TimeSpan.FromSeconds(5), Token)).Code);
        Assert.Equal(1, connector.Calls);
    }

    private static IOutputTarget Target(HttpOutputSettings settings, SharedConnections shared, IOutboundConnector connector) =>
        new HttpOutputFactory(new NoCredentials(), TimeProvider.System, connector, shared)
            .Build(new(RuntimeUnitId.Create("output.http", "route/out"), JsonSerializer.SerializeToElement(settings, Json)));
    private sealed class NoCredentials : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }

    private static OutputMessage Message(HttpOutputSettings settings, HttpCredential? credential = null) => new(
        "{\"value\":9223372036854775807}"u8.ToArray(), settings.Path, null, settings.Fingerprint(credential?.Identity), "fixture",
        settings.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D")), HttpFrozenRequest.Freeze(settings));
    private static HttpOutputSettings Settings(TcpListener server) => new()
    {
        ConnectionId = 1,
        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/api/",
        Path = "events",
        Inputs = ["device/input"],
        Headers = ImmutableDictionary<string, string>.Empty.Add("X-Static", "frozen"),
        TimeoutMs = 5000,
    };
    private static TcpListener Listen()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        return server;
    }
    private static Task<TcpClient> Accept(TcpListener server) => server.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
    private static async Task AssertClosed(Stream stream) => Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
    private sealed record Request(string Headers, byte[] Body);
    private sealed class RequestComparer : IEqualityComparer<Request>
    {
        public static RequestComparer Instance { get; } = new();
        public bool Equals(Request? x, Request? y) => x is not null && y is not null && x.Headers == y.Headers && x.Body.SequenceEqual(y.Body);
        public int GetHashCode(Request obj) => StringComparer.Ordinal.GetHashCode(obj.Headers);
    }
    private static async Task<Request> ReadRequest(NetworkStream stream)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, stop.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            {
                var header = Encoding.ASCII.GetString(bytes.ToArray());
                var length = int.Parse(header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
                Assert.InRange(length, 0, StandardEnvelope.MaximumPayloadBytes);
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, stop.Token);
                return new(header, body);
            }
        }
        throw new InvalidOperationException("fixture request headers exceeded bound");
    }
    private static Task Reply(NetworkStream stream, int status, string body, bool keepAlive = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n" +
            (keepAlive ? "" : "Connection: close\r\n") + "\r\n");
        return stream.WriteAsync(header.Concat(bytes).ToArray(), Token).AsTask();
    }

    private sealed class Connector : IOutboundConnector
    {
        private readonly OutboundConnector _inner = new(new Loopback());
        public int Calls { get; private set; }
        public bool Deny { get; init; }
        public TaskCompletionSource BodyRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Calls++;
            if (Deny)
            { throw new OutboundAccessDeniedException(); }
            return new ObservedStream(await _inner.ConnectAsync(host, port, cancellationToken), BodyRead);
        }
    }
    // 完整响应头及首个正文 x 读到后的下一次传输读取，固定“正在等剩余正文”窗口。
    // 按字节识别标记，不假定一次 TCP Read 就能得到整个响应头。
    private sealed class ObservedStream(Stream inner, TaskCompletionSource bodyRead) : Stream
    {
        private int _matched;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_matched == 5)
            { bodyRead.TrySetResult(); }
            var read = await inner.ReadAsync(buffer, cancellationToken);
            foreach (var value in buffer.Span[..read])
            {
                if (_matched == 5)
                { break; }
                _matched = value == "\r\n\r\nx"u8[_matched] ? _matched + 1 : value == 13 ? 1 : 0;
            }
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            { inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class Loopback : IOutboundAddressPolicy
    { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
}

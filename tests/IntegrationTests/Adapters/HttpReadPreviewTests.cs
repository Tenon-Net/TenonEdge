using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class HttpReadPreviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RealGetReturnsOriginalNumericTextAndUsesFormalFieldAndTimeDecoder()
    {
        using var listener = Listen();
        const string body = "{\"large\":9223372036854775807,\"bad\":\"not-number\",\"when\":\"2026-10-10T00:00:00Z\"}";
        var peer = Reply(listener, body);
        var settings = Settings(listener) with
        {
            Fields = JsonSerializer.SerializeToElement(new[] { new { key = "large", path = "/large", type = "integer" }, new { key = "bad", path = "/bad", type = "integer" }, new { key = "missing", path = "/absent", type = "string" } }),
            UpstreamTime = JsonSerializer.SerializeToElement(new { path = "/when", format = "rfc3339" })
        };
        var result = await Probe(time: new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T00:00:01Z", System.Globalization.CultureInfo.InvariantCulture))).RunAsync(settings, ClockSyncState.Synchronized, Token);
        Assert.True(result.Success);
        Assert.True(result.CanUseAsSample);
        Assert.Equal(body, result.RawJson);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("9223372036854775807", result.Fields.Single(field => field.Key == "large").Text);
        Assert.True(result.Fields.Single(field => field.Key == "large").UnsafeJavaScriptInteger);
        Assert.Equal("conversionFailed", result.Fields.Single(field => field.Key == "bad").Reason);
        Assert.DoesNotContain(result.Fields, field => field.Key == "missing");
        Assert.Equal(TimeSource.Upstream, result.TimeSource);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), result.CollectedAt);
        Assert.DoesNotContain("9223372036854775807", result.ToString(), StringComparison.Ordinal);
        await peer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullSamplePreservesLargeTextQualityOriginalJsonAndFormalTime(bool invalidTime)
    {
        using var listener = Listen();
        var text = new string('中', 40000) + "😀";
        var body = JsonSerializer.Serialize(new { text, when = invalidTime ? "invalid" : "2026-10-10T00:00:00Z" });
        var peer = Reply(listener, body);
        var settings = Settings(listener) with
        {
            Fields = JsonSerializer.Deserialize<JsonElement>(
                """[{"key":"text","path":"/text","type":"string"},{"key":"bad","path":"/text","type":"integer"},{"key":"absent","path":"/absent","type":"number"}]"""),
            UpstreamTime = JsonSerializer.Deserialize<JsonElement>("""{"path":"/when","format":"rfc3339"}""")
        };
        var receivedAt = new DateTimeOffset(2026, 10, 10, 0, 0, 1, TimeSpan.Zero);
        var result = await Probe(time: new FakeTimeProvider(receivedAt)).ReadSampleAsync(settings, "input", "device",
            ClockSyncState.NotSynchronized, Token);
        Assert.Equal("success", result.Code);
        var message = Assert.IsType<EdgeMessage>(result.Message);
        Assert.Equal(text, message.Values["text"].ToString());
        Assert.Equal(QualityCode.ConversionFailed, message.Quality["bad"]);
        Assert.False(message.Values.ContainsKey("absent"));
        Assert.Equal(body, message.RawJson!.Root.GetRawText());
        Assert.True(message.RawJson.ByteLength > 65536);
        Assert.Equal(receivedAt, message.ReceivedAt);
        Assert.Equal(invalidTime ? receivedAt : receivedAt.AddSeconds(-1), message.CollectedAt);
        Assert.Equal(invalidTime ? TimeSource.GatewayUnsynced : TimeSource.Upstream, message.TimeSource);
        Assert.Equal(invalidTime, result.InvalidUpstreamTime);
        Assert.Equal("input", message.InputId);
        Assert.Equal("device", message.DeviceKey);
        Assert.DoesNotContain(text, result.ToString(), StringComparison.Ordinal);
        await peer;
    }

    [Theory]
    [InlineData("password", "unrelated-private-value")]
    [InlineData("echo", "fixture-secret")]
    [InlineData("echo", "dXNlcjpmaXh0dXJlLXNlY3JldA==")]
    public async Task FullSampleStillRejectsSensitiveContentBeyondTheDisplayPrefix(string key, string value)
    {
        using var listener = Listen();
        var body = "{\"padding\":\"" + new string('x', 100000) + "\"," + JsonSerializer.Serialize(key) + ":" + JsonSerializer.Serialize(value) + "}";
        var peer = Reply(listener, body);
        var credential = new HttpCredential(new(HttpCredentialKind.Basic, "user", null, "account", "version"), "fixture-secret");
        var result = await Probe(credentials: new CredentialResolver(credential)).ReadSampleAsync(SampleSettings(listener), "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal("payload.redacted", result.Code);
        Assert.Null(result.Message);
        Assert.DoesNotContain(value, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        await peer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullSampleCancellationAndDeadlineCloseActualIoWithoutReturningPartialMessage(bool timeout)
    {
        using var listener = Listen();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await ReadHeaders(reader);
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{");
            accepted.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }, Token);
        var time = new FakeTimeProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = Probe(time: time).ReadSampleAsync(SampleSettings(listener), "input", "device", ClockSyncState.Unknown, stop.Token);
        await accepted.Task.WaitAsync(Token);
        if (timeout)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal("timeout", result.Code);
            Assert.Null(result.Message);
        }
        else
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await peer;
    }

    [Theory]
    [InlineData(302, "{}", "http.status")]
    [InlineData(401, "{}", "http.status")]
    [InlineData(500, "{}", "http.status")]
    [InlineData(200, "not json", "payload.invalidJson")]
    public async Task FailedStatusAndInvalidJsonDoNotReturnBodyOrFollowRedirect(int status, string body, string code)
    {
        using var listener = Listen();
        var peer = Reply(listener, body, status);
        var result = await Probe().RunAsync(Settings(listener), ClockSyncState.Unknown, Token);
        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(status, result.StatusCode);
        Assert.Null(result.RawJson);
        await peer;
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task RawAndDecodedStringsAreBoundedByActualSerializedBytes()
    {
        using var listener = Listen();
        var body = JsonSerializer.Serialize(new { value = new string('中', 90000) + "😀", large = 9223372036854775807L });
        var peer = Reply(listener, body);
        var settings = Settings(listener) with { Fields = JsonSerializer.SerializeToElement(new[] { new { key = "value", path = "/value", type = "string" } }) };
        var result = await Probe().RunAsync(settings, ClockSyncState.Unknown, Token);
        Assert.True(result.Success);
        Assert.True(result.RawTruncated);
        Assert.True(result.FieldsTruncated);
        Assert.False(result.CanUseAsSample);
        Assert.True(result.RawBytes > 64 * 1024);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, Json).Length <= JsonReadPreview.MaximumDisplayBytes);
        Assert.False(char.IsHighSurrogate(result.RawJson![^1]));
        await peer;
    }

    [Theory]
    [InlineData("{\"password\":\"unrelated-private-value\",\"large\":42}")]
    [InlineData("{\"nested\":[{\"access_token\":\"private\"}]}")]
    [InlineData("{\"echo\":\"fixture\\u002Dsecret\"}")]
    [InlineData("{\"echo\":\"dXNlcjpmaXh0dXJlLXNlY3JldA==\"}")]
    public async Task SensitiveNamesAndEncodedCredentialEchoHideBothRawAndDecodedFields(string body)
    {
        using var listener = Listen();
        var peer = Reply(listener, body);
        var credential = new HttpCredential(new(HttpCredentialKind.Basic, "user", null, "account", "version"), "fixture-secret");
        var result = await Probe(credentials: new CredentialResolver(credential)).RunAsync(Settings(listener), ClockSyncState.Unknown, Token);
        Assert.True(result.Redacted);
        Assert.False(result.CanUseAsSample);
        Assert.Null(result.RawJson);
        Assert.Empty(result.Fields);
        Assert.Equal("payload.redacted", result.Code);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result, Json), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(result, Json), StringComparison.Ordinal);
        await peer;
    }

    [Fact]
    public async Task SharedRuntimeSessionIsBorrowedAndRetainedForNextRead()
    {
        using var listener = Listen();
        var settings = Settings(listener);
        var shared = new SharedConnections();
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        await using var runtime = shared.Acquire(1, "http", identity, () => new HttpConnectionSession(Connector(), settings.TimeoutMs));
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            for (var index = 0; index < 4; index++)
            {
                await ReadHeaders(reader);
                await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 12\r\n\r\n{\"value\":42}");
            }
        }, Token);
        using (await runtime.ExecuteAsync((session, token) => session.ReadInputAsync(settings, new CredentialResolver(), token), Token))
        { }
        var result = await Probe(shared, new ForbiddenConnector()).RunAsync(settings, ClockSyncState.Unknown, Token);
        Assert.True(result.Success);
        var sample = await Probe(shared, new ForbiddenConnector()).ReadSampleAsync(SampleSettings(listener), "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal("success", sample.Code);
        Assert.Equal(42, sample.Message!.RawJson!.Root.GetProperty("value").GetInt32());
        using (await runtime.ExecuteAsync((session, token) => session.ReadInputAsync(settings, new CredentialResolver(), token), Token))
        { }
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MissingBoundCredentialFailsBeforeNetwork()
    {
        using var listener = Listen();
        var credential = new HttpCredential(new(HttpCredentialKind.Bearer, null, null, "account", "v"), "fixture-secret");
        var result = await Probe(credentials: new CredentialResolver(credential, missing: true)).RunAsync(Settings(listener), ClockSyncState.Unknown, Token);
        Assert.Equal("credential.unavailable", result.Code);
        var sample = await Probe(credentials: new CredentialResolver(credential, missing: true))
            .ReadSampleAsync(SampleSettings(listener), "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal("credential.unavailable", sample.Code);
        Assert.Null(sample.Message);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task CallerCancellationStopsStalledResponseAndReleasesSession()
    {
        using var listener = Listen();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await ReadHeaders(reader);
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{");
            accepted.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }, Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = Probe().RunAsync(Settings(listener), ClockSyncState.Unknown, stop.Token);
        await accepted.Task.WaitAsync(Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await peer;
    }

    [Fact]
    public async Task DeadlineDuringBodyReadReturnsTimeoutAndClosesActualIo()
    {
        using var listener = Listen();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await ReadHeaders(reader);
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{");
            accepted.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }, Token);
        var time = new FakeTimeProvider();
        var pending = Probe(time: time).RunAsync(Settings(listener), ClockSyncState.Unknown, Token);
        await accepted.Task.WaitAsync(Token);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("timeout", result.Code);
        Assert.Null(result.RawJson);
        await peer;
    }

    [Fact]
    public async Task OversizedDeclaredBodyIsRejectedWithoutDraining()
    {
        using var listener = Listen();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await ReadHeaders(reader);
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 10000000\r\n\r\n");
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }, Token);
        var result = await Probe().RunAsync(Settings(listener), ClockSyncState.Unknown, Token);
        Assert.Equal("payload.tooLarge", result.Code);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.RawJson);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Theory]
    [InlineData("invalidPath")]
    [InlineData("differentConnection")]
    [InlineData("duplicateKey")]
    public async Task InvalidLaterSourceRejectsEntirePreviewBeforeNetworking(string scenario)
    {
        using var listener = Listen();
        var first = Settings(listener);
        var second = scenario switch
        {
            "invalidPath" => first with { Path = "http://elsewhere.invalid/" },
            "differentConnection" => first with { ConnectionId = 2 },
            _ => first
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Probe().RunSourcesAsync(
            [new("first", first), new(scenario == "duplicateKey" ? "first" : "second", second)], ClockSyncState.Unknown, Token));
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MultipleSourcesShareOnePhysicalConnectionAndAggregateDisplayBudget()
    {
        using var listener = Listen();
        var body = JsonSerializer.Serialize(new { value = new string('中', 30000) });
        var requests = 0;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(Token) is { } line)
            {
                Assert.Equal("GET /values HTTP/1.1", line);
                while (await reader.ReadLineAsync(Token) is { Length: > 0 })
                { }
                requests++;
                await Write(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
            }
        }, Token);
        var sources = Enumerable.Range(0, 20).Select(index => new HttpPreviewSource($"source{index}", Settings(listener))).ToArray();
        var result = await Probe().RunSourcesAsync(sources, ClockSyncState.Unknown, Token);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(result.Truncated);
        Assert.False(result.DeadlineExceeded);
        Assert.Equal(20, result.TotalSources);
        Assert.InRange(result.Sources.Length, 2, 19);
        Assert.Equal(result.Sources.Length, requests);
        Assert.All(result.Sources, source => Assert.False(source.Preview.CanUseAsSample));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, Json).Length <= 65536);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task OverallDeadlineStopsBeforeNextSourceAndClosesStalledIo()
    {
        using var listener = Listen();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await ReadHeaders(reader);
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{");
            accepted.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }, Token);
        var time = new FakeTimeProvider();
        var pending = Probe(time: time).RunSourcesAsync([new("first", Settings(listener)), new("second", Settings(listener))],
            ClockSyncState.Unknown, Token, timeoutMs: 1000);
        await accepted.Task.WaitAsync(Token);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(result.DeadlineExceeded);
        Assert.Equal("timeout", Assert.Single(result.Sources).Preview.Code);
        await peer;
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task FullSampleRequiresFormalFieldsBeforeNetworkUnlikeInferenceDisplay()
    {
        using var listener = Listen();
        await Assert.ThrowsAsync<ArgumentException>(() => Probe().ReadSampleAsync(Settings(listener), "input", "device", ClockSyncState.Unknown, Token));
        Assert.False(listener.Pending());
    }

    private static HttpInputSettings SampleSettings(TcpListener listener) => Settings(listener) with
    {
        Fields = JsonSerializer.Deserialize<JsonElement>("""[{"key":"value","path":"/value","type":"integer"}]""")
    };

    private static HttpReadPreview Probe(SharedConnections? shared = null, IOutboundConnector? connector = null, IHttpCredentialResolver? credentials = null, TimeProvider? time = null) =>
        new(shared ?? new(), connector ?? Connector(), credentials ?? new CredentialResolver(), new JsonInputDecoderFactory(), time ?? TimeProvider.System);
    private static HttpInputSettings Settings(TcpListener listener) => new()
    {
        ConnectionId = 1,
        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/",
        Path = "values",
        Fields = JsonSerializer.SerializeToElement(Array.Empty<object>())
    };
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static OutboundConnector Connector() => new(new LoopbackPolicy());
    private sealed class LoopbackPolicy : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
    private sealed class CredentialResolver(HttpCredential? credential = null, bool missing = false) : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => credential?.Identity;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => missing ? null : credential;
    }
    private sealed class ForbiddenConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new InvalidOperationException("Must borrow runtime session");
    }
    private static Task Write(NetworkStream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), Token).AsTask();
    private static async Task ReadHeaders(StreamReader reader)
    {
        Assert.Equal("GET /values HTTP/1.1", await reader.ReadLineAsync(Token));
        while (await reader.ReadLineAsync(Token) is { Length: > 0 })
        { }
    }
    private static async Task Reply(TcpListener listener, string body, int status = 200)
    {
        using var socket = await listener.AcceptTcpClientAsync(Token);
        var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await ReadHeaders(reader);
        await Write(stream, $"HTTP/1.1 {status} Response\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nLocation: /must-not-follow\r\nConnection: close\r\n\r\n{body}");
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class HttpInputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly object[] Fields = [new { key = "value", path = "/v", type = "integer" }];
    private static readonly string[] Inputs = ["device/http"];

    [Theory]
    [InlineData("safe", false)]
    [InlineData("echo", true)]
    [InlineData("key", true)]
    [InlineData("encoded", true)]
    public async Task RuntimeCacheUsesRequestCredentialEvenWhenItRotatesBeforeResponse(string kind, bool blocked)
    {
        using var server = Listen();
        using var data = TemporaryDirectory.Create();
        var credentials = new RotatingCredential();
        var oldSecret = credentials.Current.Secret;
        var shared = new SharedConnections();
        await using var runtime = RuntimeTesting.CreateRuntime(
            [new HttpInputFactory(credentials, new OutboundConnector(new Loopback()), shared, new JsonInputDecoderFactory())], data.Path);
        var plan = Plan(server);
        await runtime.ActivateAsync(runtime.Prepare(new RuntimePlan(7, [plan])));
        using var socket = await server.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        var headers = await ReadHeadersAsync(socket.GetStream());
        Assert.Contains("Authorization: Bearer " + oldSecret, headers, StringComparison.Ordinal);
        credentials.Current = new(credentials.Current.Identity with { Version = "2" }, "new-fixture-secret");
        var payload = kind switch
        {
            "echo" => JsonSerializer.Serialize(new { v = 42, echo = oldSecret }),
            "encoded" => JsonSerializer.Serialize(new { v = 42, echo = Uri.EscapeDataString(oldSecret) }),
            "key" => "{\"v\":42,\"password\":\"fixture-password\"}",
            _ => "{\"v\":9223372036854775807}"
        };
        await ReplyAsync(socket.GetStream(), payload, close: true);
        await OutputPipelineTests.UntilAsync(() => runtime.Messages.Read(plan.Id, false).Count > 0);
        var record = runtime.Messages.Read(plan.Id, true)[0];
        Assert.Equal(blocked, record.PrivacyBlocked);
        Assert.Equal(!blocked, runtime.TryReadInputSample(plan.Id, record.Sequence, 7, record.SourceMessageId, out var sample));
        if (blocked)
        {
            Assert.Null(record.Body);
            Assert.Equal(0, record.StoredBytes);
            Assert.False(record.Truncated);
        }
        else
        { Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), sample!.Values["value"].ToString()); }
    }

    private sealed class RotatingCredential : IHttpCredentialResolver
    {
        internal HttpCredential Current = new(new(HttpCredentialKind.Bearer, null, null, "fixture", "1"), "old/fixture+secret");
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => Current.Identity;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => Current;
    }

    [Fact]
    public async Task RealGetProducesExactTypedValuesAndReleasesTheBudget()
    {
        using var server = Listen();
        var peer = ServeAsync(server, "{\"v\":9223372036854775807}");
        await using var harness = new Harness(server);
        var message = await harness.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), message.Values["value"].ToString());
        Assert.Equal(message.ReceivedAt, message.CollectedAt);
        Assert.Equal(long.MaxValue, message.RawJson!.Root.GetProperty("v").GetInt64());
        Assert.Equal(TimeSource.GatewayUnsynced, message.TimeSource);
        Assert.Equal(1, harness.Reads.Snapshot().ReadsCompleted);
        Assert.Equal(48L * 1024 * 1024, harness.Budget.Status.PeakReservedBytes);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
        Assert.StartsWith("GET /api/values HTTP/1.1", await peer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnselectedRawFieldsRemainChargedUntilTheInputIsConsumed()
    {
        using var server = Listen();
        var body = "{\"v\":42,\"unselected\":\"" + new string('x', 300000) + "\"}";
        var peer = ServeAsync(server, body);
        await using var harness = new Harness(server);
        await OutputPipelineTests.UntilAsync(() => harness.Channel.Reader.Count == 1);
        var retained = harness.Budget.Status.ReservedBytes;
        Assert.True(retained > 4 * 1024 * 1024);
        var message = await harness.Channel.Reader.ReadAsync(Token);
        Assert.Equal(retained, message.BudgetBytes);
        Assert.Equal(300000, message.RawJson!.Root.GetProperty("unselected").GetString()!.Length);
        Assert.Equal("42", message.Values["value"].ToString());
        Assert.Single(message.Values);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
        await peer;
    }

    [Theory]
    [InlineData("{\"v\":\"invalid\"}", false)]
    [InlineData("broken-secret-response", true)]
    public async Task FieldAndDocumentErrorsDoNotReusePreviousValues(string body, bool wholeFailure)
    {
        using var server = Listen();
        var peer = ServeAsync(server, body);
        await using var harness = new Harness(server);
        await OutputPipelineTests.UntilAsync(() => harness.Reads.Snapshot().ReadsCompleted + harness.Reads.Snapshot().ReadsFailed == 1);
        await peer;
        if (wholeFailure)
        {
            Assert.False(harness.Channel.Reader.TryRead(out _));
            Assert.Equal(1, harness.Reads.Snapshot().DataRejected);
            Assert.DoesNotContain("secret", harness.Reads.Snapshot().LastError!, StringComparison.Ordinal);
        }
        else
        {
            var message = await harness.Channel.Reader.ReadAsync(Token);
            Assert.Empty(message.Values);
            Assert.Equal(QualityCode.ConversionFailed, message.Quality["value"]);
        }
    }

    [Fact]
    public async Task NoBudgetMeansNoNetworkAndThePeriodDeadlineCountsOverrun()
    {
        using var server = Listen();
        var budget = new InFlightBudget(64L * 1024 * 1024);
        Assert.True(budget.TryReserve(budget.Capacity));
        await using var harness = new Harness(server, budget);
        Assert.False(server.Pending());
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await OutputPipelineTests.UntilAsync(() => harness.Reads.Snapshot().CyclesSkipped == 1);
        Assert.False(server.Pending());
        await harness.StopAsync();
        budget.Release(budget.Capacity);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task StoppingAHangingRequestClosesThePeerAndDoesNotCountFailure()
    {
        using var server = Listen();
        await using var harness = new Harness(server);
        using var socket = await server.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await ReadHeadersAsync(socket.GetStream());
        await harness.StopAsync();
        Assert.Equal(0, await socket.GetStream().ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(0, harness.Reads.Snapshot().ReadsFailed);
        Assert.Equal(0, harness.Reads.Snapshot().CyclesSkipped);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task ARequestCannotOverlapTheNextCycleAndTimeoutIsAReadFailure()
    {
        using var server = Listen();
        await using var harness = new Harness(server);
        using var first = await server.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await ReadHeadersAsync(first.GetStream());
        harness.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(server.Pending());
        Assert.Equal(0, harness.Reads.Snapshot().ReadsFailed);
        harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        await OutputPipelineTests.UntilAsync(() => harness.Reads.Snapshot().ReadsFailed == 1);
        Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        using var next = await server.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await ReadHeadersAsync(next.GetStream());
        await ReplyAsync(next.GetStream(), "{\"v\":42}", close: true);
        var message = await harness.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("42", message.Values["value"].ToString());
        Assert.Equal(1, harness.Reads.Snapshot().ReadsFailed);
    }

    [Theory]
    [InlineData("rfc3339", "\"2025-01-02T03:04:05Z\"", ClockSyncState.Synchronized, "2025-01-02T03:04:05Z", TimeSource.Upstream, 0)]
    [InlineData("unixSeconds", "0", ClockSyncState.Synchronized, "1970-01-01T00:00:00Z", TimeSource.Upstream, 0)]
    [InlineData("unixMilliseconds", "1000", ClockSyncState.Synchronized, "1970-01-01T00:00:01Z", TimeSource.Upstream, 0)]
    [InlineData("rfc3339", "null", ClockSyncState.Synchronized, "2026-10-08T00:00:00Z", TimeSource.Gateway, 1)]
    [InlineData("rfc3339", "\"secret-invalid-time\"", ClockSyncState.NotSynchronized, "2026-10-08T00:00:00Z", TimeSource.GatewayUnsynced, 1)]
    [InlineData("rfc3339", "\"2026-10-08T00:05:00Z\"", ClockSyncState.Synchronized, "2026-10-08T00:05:00Z", TimeSource.Upstream, 0)]
    [InlineData("rfc3339", "\"2026-10-08T00:05:01Z\"", ClockSyncState.Synchronized, "2026-10-08T00:00:00Z", TimeSource.Gateway, 1)]
    [InlineData("rfc3339", "\"2026-10-08T00:05:01Z\"", ClockSyncState.Unknown, "2026-10-08T00:05:01Z", TimeSource.Upstream, 0)]
    public async Task UpstreamTimesUseExplicitFormatsAndInvalidTimesFallBackWithoutDroppingValues(
        string format, string timeJson, ClockSyncState clock, string expectedAt, TimeSource expectedSource, long invalid)
    {
        using var server = Listen();
        var peer = ServeAsync(server, "{\"v\":42,\"sampleAt\":" + timeJson + "}");
        await using var harness = new Harness(server, upstreamTime: new { path = "/sampleAt", format }, clock: clock);
        var message = await harness.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(DateTimeOffset.Parse(expectedAt, CultureInfo.InvariantCulture), message.CollectedAt);
        Assert.Equal(expectedSource, message.TimeSource);
        Assert.Equal("42", message.Values["value"].ToString());
        Assert.Empty(message.Quality);
        Assert.Equal(1, harness.Reads.Snapshot().ReadsCompleted);
        Assert.Equal(0, harness.Reads.Snapshot().ReadsFailed);
        Assert.Equal(invalid, harness.Reads.Snapshot().InvalidUpstreamTimes);
        Assert.Equal(InputReadHealth.Normal, harness.Reads.Snapshot().Health);
        await peer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductHostGetsAndPostsThroughOneSharedAuthenticatedConnection(bool invalidTime)
    {
        using var server = Listen();
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var api = bootstrap.CreateClient();
            using var setup = await api.CompleteSetupAsync(Token);
            await api.UseSessionAsync(setup, Token);
            await OutputPipelineTests.UntilAsync(() => bootstrap.Services.GetRequiredService<DeliveryCache>().GetStatus().Store is not null);
            using var saved = await api.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/1/http-credential", UriKind.Relative),
                new { recipient = Base(server), kind = "bearer", secret = "fixture-get-token" }, Token);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var seed = Path.Combine(seeds.Path, "get-post.json");
        var input = Plan(server, new { path = "/sampleAt", format = "rfc3339", source = "device" });
        var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "out"), JsonSerializer.SerializeToElement(new
        { connectionId = 1, baseAddress = Base(server), path = "events", timeoutMs = 5000, inputs = Inputs, mapping = new[] { new { target = "mapped", type = "integer", sourcePath = "/v", required = true } } }));
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        { schemaVersion = 1, units = new[] { input, output }.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }) }), Token);
        var peer = Task.Run(async () =>
        {
            // 同一 keep-alive TCP 接收 GET 和 POST；如果创建了第二套会话，此用例无法完成。
            using var socket = await server.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            var get = await ReadHeadersAsync(stream);
            Assert.StartsWith("GET /api/values HTTP/1.1", get, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer fixture-get-token", get, StringComparison.Ordinal);
            await ReplyAsync(stream, "{\"v\":9223372036854775807,\"sampleAt\":\"" + (invalidTime ? "fixture-invalid-time" : "2025-01-02T03:04:05Z") + "\",\"private\":\"raw-input-private\"}", close: false);
            var post = await ReadHeadersAsync(stream);
            Assert.StartsWith("POST /api/events HTTP/1.1", post, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer fixture-get-token", post, StringComparison.Ordinal);
            var length = int.Parse(post.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, Token);
            using var body = JsonDocument.Parse(bytes);
            Assert.Equal(long.MaxValue, body.RootElement.GetProperty("values").GetProperty("mapped").GetInt64());
            Assert.DoesNotContain("raw-input-private", body.RootElement.GetRawText(), StringComparison.Ordinal);
            if (invalidTime)
            { Assert.StartsWith("gateway", body.RootElement.GetProperty("timeSource").GetString(), StringComparison.Ordinal); }
            else
            {
                Assert.Equal("device", body.RootElement.GetProperty("timeSource").GetString());
                Assert.Equal("2025-01-02T03:04:05.000Z", body.RootElement.GetProperty("collectedAt").GetString());
            }
            await stream.WriteAsync("HTTP/1.1 204 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), Token);
        }, Token);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { [RuntimeSettings.SeedSnapshotKey] = seed, ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) });
        using var started = host.CreateClient();
        await peer.WaitAsync(TimeSpan.FromSeconds(15), Token);
        await OutputPipelineTests.UntilAsync(() => host.Services.GetRequiredService<DeliveryCache>().GetStatus().DeletedRecords >= 1);
        using var admin = await host.CreateLoggedInClientAsync("admin", Token);
        var status = await admin.GetJsonAsync("/api/edge-admin/v1/runtime/status", Token);
        var inputStatus = Assert.Single(status.GetProperty("runtime").GetProperty("units").EnumerateArray()
, unit => unit.TryGetProperty("reads", out var reads) && reads.ValueKind == JsonValueKind.Object);
        Assert.Equal(invalidTime ? 1 : 0, inputStatus.GetProperty("reads").GetProperty("invalidUpstreamTimes").GetInt64());
        Assert.DoesNotContain("fixture-invalid-time", status.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baseAddress", "http://other.example/")]
    [InlineData("timeoutMs", 2000)]
    public void ConflictingSharedConnectionConfigurationIsRejectedBeforeFreezing(string property, object value)
    {
        using var server = Listen();
        var input = Plan(server);
        var output = new Dictionary<string, object> { ["connectionId"] = 1, ["baseAddress"] = Base(server), ["timeoutMs"] = 5000, ["inputs"] = Inputs };
        output[property] = value;
        var units = new[]
        {
            new { kind = input.Id.Kind, key = input.Id.Key, configuration = input.Configuration },
            new { kind = "output.http", key = "out", configuration = JsonSerializer.SerializeToElement(output) },
        };
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units })));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly IRuntimeUnit _unit;
        private readonly Task _run;
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        public InFlightBudget Budget { get; }
        public BudgetedInputChannel Channel { get; }
        public InputReadStatus Reads { get; } = new("device", "http", new RecordingEventSink());
        public Harness(TcpListener server, InFlightBudget? budget = null, object? upstreamTime = null, ClockSyncState clock = ClockSyncState.NotSynchronized)
        {
            Budget = budget ?? new InFlightBudget(64L * 1024 * 1024);
            Channel = new BudgetedInputChannel(1, Budget);
            var context = new InputUnitContext("device", "http", Time, new FixedClockSyncStatus(clock), Reads, (InputMessageWriter)Channel.Writer);
            _unit = new HttpInputFactory(new Anonymous(), new OutboundConnector(new Loopback()), new SharedConnections(), new JsonInputDecoderFactory()).Build(Plan(server, upstreamTime), context);
            _run = _unit.RunAsync(_stop.Token);
        }
        public async Task StopAsync() { await _stop.CancelAsync(); await _run.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            while (Channel.Reader.TryRead(out _))
            { }
            await _unit.DisposeAsync();
            _stop.Dispose();
        }
    }
    private sealed class Anonymous : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private sealed class Loopback : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
    private static RuntimeUnitPlan Plan(TcpListener server, object? upstreamTime = null) => new(RuntimeUnitId.Create("input.http", "device/http"), JsonSerializer.SerializeToElement(new
    { connectionId = 1, baseAddress = Base(server), path = "values", periodMs = 1000, timeoutMs = 5000, fields = Fields, upstreamTime }));
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static string Base(TcpListener listener) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/api/";
    private static async Task<string> ServeAsync(TcpListener listener, string body)
    {
        using var socket = await listener.AcceptTcpClientAsync(Token);
        var headers = await ReadHeadersAsync(socket.GetStream());
        await ReplyAsync(socket.GetStream(), body, close: true);
        return headers;
    }
    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, deadline.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            { return Encoding.UTF8.GetString(bytes.ToArray()); }
        }
        throw new InvalidOperationException("Test request headers too large.");
    }
    private static Task ReplyAsync(NetworkStream stream, string body, bool close) => stream.WriteAsync(Encoding.UTF8.GetBytes(
        $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{(close ? "Connection: close\r\n" : "")}\r\n{body}"), Token).AsTask();
}

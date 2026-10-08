using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class HttpOutputTests
{
    private static readonly string[] Inputs = ["device/input"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HttpCredentialKind.Basic)]
    [InlineData(HttpCredentialKind.Bearer)]
    [InlineData(HttpCredentialKind.ApiKeyHeader)]
    [InlineData(HttpCredentialKind.ApiKeyQuery)]
    public async Task FrozenPostAndIdempotencyUseCurrentCredentialsOnEachRetry(HttpCredentialKind kind)
    {
        using var server = Listen();
        var credentials = new Credentials { Value = new(new(kind, kind == HttpCredentialKind.Basic ? "user" : null, kind is HttpCredentialKind.ApiKeyHeader or HttpCredentialKind.ApiKeyQuery ? "X-Key" : null, "account", "v1"), "old-secret") };
        var connections = new SharedConnections();
        var factory = new HttpOutputFactory(credentials, TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), connections);
        var target = factory.Build(Plan(server, "events"));
        var deliveryId = Guid.NewGuid();
        var message = target.CreateMessage(Message(), deliveryId);
        Assert.Null(message.QualityOfService);
        Assert.Equal(deliveryId.ToString("D"), message.Headers!["Idempotency-Key"]);
        Assert.DoesNotContain("old-secret", message.FingerprintSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("old-secret", Encoding.UTF8.GetString(message.Payload.Span), StringComparison.Ordinal);
        await using var adapter = target.CreateAdapter();
        var first = ServeOnceAsync(server, 503);
        Assert.Equal(OutputSendStatus.Retry, (await adapter.SendAsync(message, Token)).Status);
        var old = await first;
        credentials.Value = credentials.Value with { Identity = credentials.Value.Identity with { Version = "v2" }, Secret = "new-secret" };
        var second = ServeOnceAsync(server, 204);
        Assert.Equal(OutputSendStatus.Ack, (await adapter.SendAsync(message, Token)).Status);
        var current = await second;
        Assert.Equal(old.Body, current.Body);
        Assert.Contains("POST /api/events", old.Header, StringComparison.Ordinal);
        Assert.Contains($"Idempotency-Key: {deliveryId:D}", current.Header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Static: frozen", current.Header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("application/json", current.Header, StringComparison.OrdinalIgnoreCase);
        var expectedOld = kind == HttpCredentialKind.Basic ? Convert.ToBase64String(Encoding.UTF8.GetBytes("user:old-secret")) : "old-secret";
        var expectedNew = kind == HttpCredentialKind.Basic ? Convert.ToBase64String(Encoding.UTF8.GetBytes("user:new-secret")) : "new-secret";
        Assert.Contains(expectedOld, old.Header, StringComparison.Ordinal);
        Assert.Contains(expectedNew, current.Header, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedOld, current.Header, StringComparison.Ordinal);
        Assert.Equal(target.CurrentFingerprint, message.Fingerprint);
        Assert.Equal(Base(server), credentials.LastRecipient);
    }

    [Fact]
    public async Task DifferentPathsShareOneSessionWithoutKeepingTheFirstBorrowersPath()
    {
        using var server = Listen();
        var factory = new HttpOutputFactory(new Credentials(), TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections());
        var first = factory.Build(Plan(server, "first"));
        var second = factory.Build(Plan(server, "second"));
        var peer = Task.Run(async () =>
        {
            using var socket = await server.AcceptTcpClientAsync(Token);
            var one = await ReadAsync(socket.GetStream());
            await ReplyAsync(socket.GetStream(), 204, close: false);
            var two = await ReadAsync(socket.GetStream());
            await ReplyAsync(socket.GetStream(), 204, close: true);
            return (one, two);
        }, Token);
        var a = first.CreateAdapter();
        await using var b = second.CreateAdapter();
        Assert.Equal(OutputSendStatus.Ack, (await a.SendAsync(first.CreateMessage(Message(), Guid.NewGuid()), Token)).Status);
        await a.DisposeAsync();
        Assert.Equal(OutputSendStatus.Ack, (await b.SendAsync(second.CreateMessage(Message(), Guid.NewGuid()), Token)).Status);
        var observed = await peer.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Contains("POST /api/first ", observed.one.Header, StringComparison.Ordinal);
        Assert.Contains("POST /api/second ", observed.two.Header, StringComparison.Ordinal);
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task ChangedAccountAndMissingCredentialsNeverConnectOrCountAsPoison()
    {
        using var server = Listen();
        var credentials = new Credentials { Value = new(new(HttpCredentialKind.Bearer, null, null, "a", "v1"), "secret") };
        var target = new HttpOutputFactory(credentials, TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections()).Build(Plan(server, "events"));
        var message = target.CreateMessage(Message(), Guid.NewGuid());
        await using var adapter = target.CreateAdapter();
        credentials.Value = credentials.Value with { Identity = credentials.Value.Identity with { AccountMarker = "b", Version = "v2" } };
        var mismatch = await adapter.SendAsync(message, Token);
        Assert.Equal(OutputSendStatus.SuspendTarget, mismatch.Status);
        Assert.True(mismatch.FingerprintMismatch);
        Assert.False(mismatch.CountsTowardPoison);
        credentials.Available = false;
        Assert.Equal(OutputSendStatus.SuspendTarget, (await adapter.SendAsync(message, Token)).Status);
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task RuntimePersistsFrozenHeadersAndReplaysTheSamePostAfterRestart()
    {
        using var directory = TemporaryDirectory.Create();
        using var server = Listen();
        var credentials = new Credentials { Value = new(new(HttpCredentialKind.Bearer, null, null, "account", "v1"), "not-in-queue") };
        var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.test", "device/input"), JsonSerializer.SerializeToElement(new { }));
        (string Header, byte[] Body) original;
        await using (var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path,
            outputs: [new HttpOutputFactory(credentials, TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections())]))
        {
            await using var plan = runtime.Prepare(new RuntimePlan(1, [input, Plan(server, "events")]));
            Assert.Empty(plan.Failures);
            var peer = ServeOnceAsync(server, 401);
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            original = await peer.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await runtime.StopAsync(Token);
            var pending = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Null(pending.QualityOfService);
            Assert.True(runtime.InFlight.Status.PeakReservedBytes >= 2L * StandardEnvelope.MaximumPayloadBytes + 128 * 1024);
            Assert.Equal("frozen", pending.Headers!["X-Static"]);
            Assert.Equal(pending.DeliveryId.ToString("D"), pending.Headers["Idempotency-Key"]);
            Assert.DoesNotContain("not-in-queue", JsonSerializer.Serialize(pending.Headers), StringComparison.Ordinal);
        }
        await using var restarted = RuntimeTesting.CreateRuntime([new OneShotInputFactory(0)], directory.Path,
            outputs: [new HttpOutputFactory(credentials, TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections())]);
        var updated = Plan(server, "events");
        var configuration = updated.Configuration.GetRawText().Replace("frozen", "changed", StringComparison.Ordinal);
        updated = new(updated.Id, JsonSerializer.Deserialize<JsonElement>(configuration));
        await using var restartedPlan = restarted.Prepare(new RuntimePlan(2, [input, updated]));
        Assert.Empty(restartedPlan.Failures);
        var replay = ServeOnceAsync(server, 204);
        restarted.StartCore();
        await restarted.ActivateAsync(restartedPlan);
        var observed = await replay.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(original.Body, observed.Body);
        Assert.Contains("X-Static: frozen", observed.Header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Static: changed", observed.Header, StringComparison.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (restarted.Deliveries.ReadPending(1, 0, 1).Count != 0)
        { await Task.Delay(10, deadline.Token); }
        await restarted.StopAsync(Token);
        Assert.Equal(0, restarted.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task IndependentHttpConnectionsToSameEndpointDoNotInventMqttClientIdConflicts()
    {
        using var directory = TemporaryDirectory.Create();
        using var server = Listen();
        var factory = new HttpOutputFactory(new Credentials(), TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections());
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory(0)], directory.Path, outputs: [factory]);
        var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.test", "device/input"), JsonSerializer.SerializeToElement(new { }));
        var first = Plan(server, "events");
        var second = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "second"), JsonSerializer.SerializeToElement(new
        {
            connectionId = 2,
            baseAddress = Base(server),
            path = "events",
            inputs = Inputs
        }));
        await using var plan = runtime.Prepare(new RuntimePlan(1, [input, first, second]));
        Assert.Empty(plan.Failures);
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task UnresponsiveMappedTargetDoesNotBlockAnotherTargetsAcknowledgement()
    {
        using var directory = TemporaryDirectory.Create();
        using var slowServer = Listen();
        using var healthyServer = Listen();
        var inputFactory = new OneShotInputFactory();
        var factory = new HttpOutputFactory(new Credentials(), TimeProvider.System,
            new OutboundConnector(new LoopbackPolicy()), new SharedConnections());
        await using var runtime = RuntimeTesting.CreateRuntime([inputFactory], directory.Path, outputs: [factory]);
        var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.test", "device/input"), JsonSerializer.SerializeToElement(new { }));
        RuntimeUnitPlan MappedTarget(TcpListener server, long connectionId, string target, int scale) =>
            new(RuntimeUnitId.Create("output.http", target), JsonSerializer.SerializeToElement(new
            {
                connectionId,
                baseAddress = Base(server),
                path = "events",
                inputs = Inputs,
                timeoutMs = 30000,
                mapping = new[] { new { target, type = "integer", sourceField = "value", scale } },
            }));
        await using var plan = runtime.Prepare(new RuntimePlan(1,
            [input, MappedTarget(slowServer, 1, "slow", 2), MappedTarget(healthyServer, 2, "healthy", 3)]));
        Assert.Empty(plan.Failures);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowPeer = Task.Run(async () =>
        {
            using var socket = await slowServer.AcceptTcpClientAsync(Token);
            var request = await ReadAsync(socket.GetStream());
            received.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            await ReplyAsync(socket.GetStream(), 503, close: true);
            return request;
        }, Token);
        var healthyPeer = ServeOnceAsync(healthyServer, 204);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        try
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var healthy = await healthyPeer.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(2, 0, 1).Count == 0);
            Assert.False(slowPeer.IsCompleted);
            var pending = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2));
            using var frozen = JsonDocument.Parse(pending.Payload);
            using var delivered = JsonDocument.Parse(healthy.Body);
            Assert.Equal(84, frozen.RootElement.GetProperty("values").GetProperty("slow").GetInt64());
            Assert.Equal(126, delivered.RootElement.GetProperty("values").GetProperty("healthy").GetInt64());
            Assert.Equal(frozen.RootElement.GetProperty("sourceMessageId").GetString(),
                delivered.RootElement.GetProperty("sourceMessageId").GetString());
            Assert.Equal(1, inputFactory.Started);
        }
        finally
        {
            release.TrySetResult();
            await slowPeer.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await runtime.StopAsync(Token);
        }
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2));
        Assert.Empty(runtime.Deliveries.ReadPending(2, 0, 2));
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    private static RuntimeUnitPlan Plan(TcpListener server, string path) => new(RuntimeUnitId.Create("output.http", path),
        JsonSerializer.SerializeToElement(new { connectionId = 1, baseAddress = Base(server), path, inputs = Inputs, headers = new Dictionary<string, string> { ["X-Static"] = "frozen" } }));
    [Fact]
    public async Task HostUsesProtectedApiCredentialAndEffectiveSeedForRealHttpPost()
    {
        using var server = Listen();
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = bootstrap.CreateClient();
            using var setup = await client.CompleteSetupAsync(Token);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            await client.UseSessionAsync(setup, Token);
            await OutputPipelineTests.UntilAsync(() => bootstrap.Services.GetRequiredService<DeliveryCache>().GetStatus().Store is not null);
            using var credential = await client.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/1/http-credential", UriKind.Relative),
                new { recipient = Base(server), kind = "bearer", secret = "fixture-http-token" }, Token);
            Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
        }
        var seed = Path.Combine(seeds.Path, "http-output.json");
        var simulator = new RuntimeUnitPlan(RuntimeUnitId.Create("input.simulator", "device/input"),
            JsonSerializer.SerializeToElement(new { periodMs = 1000, sample = new { value = 42 } }));
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new[] { simulator, Plan(server, "events") }.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }),
        }), Token);
        var received = ServeOnceAsync(server, 204);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            [RuntimeSettings.SeedSnapshotKey] = seed,
            ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
        });
        using var started = host.CreateClient();
        var wire = await received.WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Contains("Authorization: Bearer fixture-http-token", wire.Header, StringComparison.Ordinal);
        Assert.Contains("POST /api/events", wire.Header, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(wire.Body);
        Assert.Equal(42, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.DoesNotContain("fixture-http-token", versions.Load(versions.ReadPointers().EffectiveRevision!.Value)!.Content, StringComparison.Ordinal);
        await OutputPipelineTests.UntilAsync(() => host.Services.GetRequiredService<DeliveryCache>().GetStatus().DeletedRecords >= 1);
    }

    private static string Base(TcpListener server) => $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/api/";
    private static EdgeMessage Message()
    {
        var at = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "input", "device", at, TimeSource.Gateway, at, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(long.MaxValue) });
    }
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static async Task<(string Header, byte[] Body)> ServeOnceAsync(TcpListener listener, int status)
    {
        using var socket = await listener.AcceptTcpClientAsync(Token);
        var request = await ReadAsync(socket.GetStream());
        await ReplyAsync(socket.GetStream(), status, close: true);
        return request;
    }
    private static Task ReplyAsync(NetworkStream stream, int status, bool close) => stream.WriteAsync(Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\n{(close ? "Connection: close\r\n" : "")}\r\n"), Token).AsTask();
    private static async Task<(string Header, byte[] Body)> ReadAsync(NetworkStream stream)
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
            { break; }
        }
        var header = Encoding.UTF8.GetString(bytes.ToArray());
        var length = int.Parse(header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, deadline.Token);
        return (header, body);
    }
    private sealed class LoopbackPolicy : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
    private sealed class Credentials : IHttpCredentialResolver
    {
        public HttpCredential? Value { get; set; }
        public bool Available { get; set; } = true;
        public string? LastRecipient { get; private set; }
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => Value?.Identity;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) { LastRecipient = recipient; return Available ? Value : null; }
    }
}

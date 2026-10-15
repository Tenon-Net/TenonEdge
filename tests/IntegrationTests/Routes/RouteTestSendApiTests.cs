using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteTestSendApiTests(MosquittoBrokerFixture brokerFixture) : IClassFixture<MosquittoBrokerFixture>
{
    private const string Preview = "/api/edge-admin/v1/routes/preview";
    private const string Prepare = "/api/edge-admin/v1/routes/test-send/prepare";
    private const string Confirm = "/api/edge-admin/v1/routes/test-send/confirm";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", false)]
    [InlineData("Configurator", false)]
    [InlineData("Configurator", true)]
    public async Task PreviewAndReviewDoNotSendThenOneConfirmationPostsTheOriginalFullBytesAndIds(string role, bool large)
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server), large: large);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, output) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        Assert.False(server.Pending());
        Assert.Equal(large, review.GetProperty("previewTruncated").GetBoolean());
        var payload = large ? JsonSerializer.Serialize(new { first = new string('x', 50000), second = new string('x', 50000) }) : output.GetProperty("body").GetString()!;
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), review.GetProperty("bodyBytes").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), review.GetProperty("sha256").GetString());
        var sending = client.PostJsonAsync(Confirm, Confirmation(review), Token);
        using var peer = await server.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        var wire = await ReadRequest(peer.GetStream());
        Assert.Equal(payload, wire.Body);
        Assert.Contains("Idempotency-Key: " + output.GetProperty("deliveryId").GetGuid().ToString("D"), wire.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("POST /events HTTP/1.1\r\n", wire.Headers, StringComparison.Ordinal);
        // 开始审计已落盘，而网络尚未返回；没有跨网络持有配置事务。
        Assert.Single(Audits(factory.Services, "route.testSend.start"));
        using (var duplicate = await client.PostJsonAsync(Confirm, Confirmation(review), Token))
        { Assert.Equal(HttpStatusCode.NotFound, duplicate.StatusCode); }
        await peer.GetStream().WriteAsync("HTTP/1.1 422 Rejected\r\nContent-Type: application/json\r\nContent-Length: 13\r\n\r\n{\"value\":123}"u8.ToArray(), Token);
        using var response = await sending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var result = await response.ReadJsonAsync(Token);
        Assert.Equal("http.response", result.GetProperty("code").GetString());
        Assert.Equal(422, result.GetProperty("http").GetProperty("statusCode").GetInt32());
        Assert.Equal("{\"value\":123}", result.GetProperty("http").GetProperty("body").GetString());
        Assert.Contains("httpStatus=422", Assert.Single(Audits(factory.Services, "route.testSend.complete")).Detail!, StringComparison.Ordinal);
        Assert.All(Audits(factory.Services, "route.testSend.complete"), audit => Assert.DoesNotContain("value", audit.Detail ?? "", StringComparison.Ordinal));
        Assert.Equal(seed.Request.ExpectedVersion, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Empty(factory.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.False(server.Pending());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("viewer")]
    [InlineData("anonymous")]
    [InlineData("csrf")]
    public async Task OwnershipRolesAndCsrfAreCheckedForBothActions(string scenario)
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server));
        TestAccounts.Create(factory.Services, "owner", ApiRequests.StrongPassword, UserRole.Configurator);
        using var owner = await factory.CreateLoggedInClientAsync("owner", Token);
        var (id, _) = await PreviewOutput(owner, seed.Request);
        var review = await Review(owner, id);
        TestAccounts.Create(factory.Services, "other", ApiRequests.StrongPassword, scenario == "viewer" ? UserRole.Viewer : UserRole.Configurator);
        using var client = scenario == "anonymous" ? factory.CreateClient() : await factory.CreateLoggedInClientAsync("other", Token);
        if (scenario == "csrf")
        { client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
        var expected = scenario == "foreign" ? HttpStatusCode.NotFound : scenario == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        using var preparing = await client.PostJsonAsync(Prepare, new { id }, Token);
        using var confirming = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(expected, preparing.StatusCode);
        Assert.Equal(expected, confirming.StatusCode);
        Assert.False(server.Pending());
        Assert.Empty(Audits(factory.Services, "route.testSend.start"));
        // 错误会话不能消费所有者的确认。
        Assert.Equal(id, (await Review(owner, id)).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("credential")]
    [InlineData("audit")]
    public async Task ChangedDraftOrCredentialAndFailedDurableStartNeverSendAndCannotReplay(string scenario)
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, _) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        if (scenario == "stale")
        {
            var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
            store.Write(null, new(store.Read().Version, "extra", "Extra", ConnectionKind.Http, Http: new("http://127.0.0.1:1/")), "fixture", null);
        }
        if (scenario == "credential")
        {
            factory.Services.GetRequiredService<HttpCredentialStore>().Write(seed.ConnectionId,
                new($"http://127.0.0.1:{Port(server)}/", Kind: TenonEdge.Abstractions.Contracts.HttpCredentialKind.Bearer,
                    Secret: "fixture-secret", ExpectedDraftVersion: seed.Request.ExpectedVersion), "fixture", null, true);
        }
        if (scenario == "audit")
        {
            factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("CREATE TRIGGER fail_test_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'route.testSend.start' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        }
        using var response = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(scenario == "audit" ? HttpStatusCode.InternalServerError : HttpStatusCode.Conflict, response.StatusCode);
        using var again = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.False(server.Pending());
        Assert.Empty(Audits(factory.Services, "route.testSend.start"));
    }

    [Fact]
    public async Task SharedBudgetRejectsBeforeConsumingAndForgedBodyOrDestinationAreNotAccepted()
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, _) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using (budget.TryAcquire(key))
        using (budget.TryAcquire(key))
        {
            using var preparing = await client.PostJsonAsync(Prepare, new { id }, Token);
            using var confirming = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, preparing.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, confirming.StatusCode);
        }
        using var forged = await client.PostJsonAsync(Confirm, new { id, confirmationId = review.GetProperty("confirmationId").GetGuid(), confirmed = true, body = "{}", address = "http://bad.invalid/" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var unconfirmed = await client.PostJsonAsync(Confirm, new { id, confirmationId = review.GetProperty("confirmationId").GetGuid(), confirmed = false }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal(id, (await Review(client, id)).GetProperty("id").GetGuid());
        Assert.False(server.Pending());
    }

    [Fact]
    public async Task DeniedDestinationIsAuditedWithoutQueueOrRetryAndLostResponseIsConsumed()
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, _) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        var policy = factory.Services.GetRequiredService<OutboundPolicyStore>();
        policy.Write(new([], [], policy.Read().Version), "fixture", null);
        using var response = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("outbound.denied", (await response.ReadJsonAsync(Token)).GetProperty("code").GetString());
        Assert.False(server.Pending());
        Assert.Equal("outbound.denied", Assert.Single(Audits(factory.Services, "route.testSend.complete")).Result);
        policy.Write(new([], [Port(server)], policy.Read().Version), "fixture", null);
        (id, _) = await PreviewOutput(client, seed.Request);
        review = await Review(client, id);
        var pending = client.PostJsonAsync(Confirm, Confirmation(review), Token);
        using (var peer = await server.AcceptTcpClientAsync(Token))
        { _ = await ReadRequest(peer.GetStream()); } // POST已经到达，但不返回任何HTTP响应。
        using var lost = await pending;
        Assert.Equal("http.deliveryUnknown", (await lost.ReadJsonAsync(Token)).GetProperty("code").GetString());
        using var replay = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.False(server.Pending());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "RealBroker")]
    public async Task MqttConfirmationPublishesExactlyThePreviewBytesWithActualAcknowledgement(int qos)
    {
        var broker = brokerFixture.Require();
        var topic = MqttProbe.NewTopic("route-send");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "route-receiver-" + Guid.NewGuid().ToString("N")), Token);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            await using var factory = new TenonEdgeApplicationFactory();
            var seed = Seed(factory.Services, broker.Port, mqtt: true, topic: topic, qos: qos);
            factory.Services.GetRequiredService<MqttCredentialStore>().Write(seed.ConnectionId,
                new($"mqtt://127.0.0.1:{broker.Port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: seed.Request.ExpectedVersion), "fixture", null, true);
            TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
            using var client = await factory.CreateLoggedInClientAsync("operator", Token);
            var (id, output) = await PreviewOutput(client, seed.Request);
            var review = await Review(client, id);
            Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
            using var sent = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            var result = await sent.ReadJsonAsync(Token);
            Assert.Equal(qos == 1 ? "mqtt.puback" : "mqtt.writtenWithoutAck", result.GetProperty("code").GetString());
            Assert.Equal(qos == 1, result.GetProperty("mqtt").GetProperty("brokerAcknowledged").GetBoolean());
            var wire = await received.NextAsync(Token);
            Assert.Equal(output.GetProperty("body").GetString(), Encoding.UTF8.GetString(wire.ApplicationMessage.Payload.ToArray()));
            Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
            Assert.Single(Audits(factory.Services, "route.testSend.start"));
            Assert.Contains(qos == 1 ? "brokerAcknowledged=true" : "brokerAcknowledged=false",
                Assert.Single(Audits(factory.Services, "route.testSend.complete")).Detail!, StringComparison.Ordinal);
            Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        }
    }

    [Fact]
    public async Task BoundHttpCredentialIsUsedButItsEchoIsNotReturnedOrAudited()
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server));
        factory.Services.GetRequiredService<HttpCredentialStore>().Write(seed.ConnectionId,
            new($"http://127.0.0.1:{Port(server)}/", Kind: TenonEdge.Abstractions.Contracts.HttpCredentialKind.Bearer,
                Secret: "fixture-secret", ExpectedDraftVersion: seed.Request.ExpectedVersion), "fixture", null, true);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, _) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        Assert.Equal("bearer", review.GetProperty("authentication").GetString());
        Assert.DoesNotContain("fixture-secret", review.ToString(), StringComparison.Ordinal);
        var pending = client.PostJsonAsync(Confirm, Confirmation(review), Token);
        using var peer = await server.AcceptTcpClientAsync(Token);
        Assert.Contains("Authorization: Bearer fixture-secret", (await ReadRequest(peer.GetStream())).Headers, StringComparison.Ordinal);
        await peer.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 14\r\n\r\nfixture-secret"u8.ToArray(), Token);
        using var response = await pending;
        var result = await response.ReadJsonAsync(Token);
        Assert.Equal("redacted", result.GetProperty("http").GetProperty("bodyState").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("http").GetProperty("body").ValueKind);
        Assert.DoesNotContain("fixture-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", Assert.Single(Audits(factory.Services, "route.testSend.complete")).Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAfterPostConsumesConfirmationAndHoldsBudgetUntilActualOperationExits()
    {
        using var server = Listen();
        await using var factory = new TenonEdgeApplicationFactory();
        var seed = Seed(factory.Services, Port(server));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, _) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = client.PostJsonAsync(Confirm, Confirmation(review), stop.Token);
        using var peer = await server.AcceptTcpClientAsync(Token);
        _ = await ReadRequest(peer.GetStream());
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (Audits(factory.Services, "route.testSend.complete").Count == 0 && DateTimeOffset.UtcNow < deadline)
        { await Task.Delay(10, Token); }
        Assert.Equal("canceledUnknown", Assert.Single(Audits(factory.Services, "route.testSend.complete")).Result);
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        using var replay = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.False(server.Pending());
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire(key);
        using var second = budget.TryAcquire(key);
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DeadlineBeforeDurableStartIsClassifiedWithoutSendingOrRestoringConfirmation()
    {
        using var server = Listen();
        await using var original = new TenonEdgeApplicationFactory();
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<RouteTestSendService>();
            // Only the test sender's deadline is advanced; authentication, artifacts and audit keep the real clock.
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<RouteTestSendService>(provider, new ImmediateDeadlineTime()));
        }));
        var seed = Seed(factory.Services, Port(server));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var (id, _) = await PreviewOutput(client, seed.Request);
        var review = await Review(client, id);
        using var response = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("deadlineUnknown", (await response.ReadJsonAsync(Token)).GetProperty("code").GetString());
        Assert.Empty(Audits(factory.Services, "route.testSend.start"));
        Assert.Empty(Audits(factory.Services, "route.testSend.complete"));
        Assert.False(server.Pending());
        using var replay = await client.PostJsonAsync(Confirm, Confirmation(review), Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
    }

    private sealed class ImmediateDeadlineTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(30), dueTime);
            var clock = new FakeTimeProvider();
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            clock.Advance(dueTime);
            return timer;
        }
    }

    private static (RoutePreviewRequest Request, long ConnectionId) Seed(IServiceProvider services, int port, bool large = false, bool mqtt = false, string? topic = null, int qos = 1)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var output = connections.Write(null, new(connections.Read().Version, "output", "Output", mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
            Http: mqtt ? null : new($"http://127.0.0.1:{port}/"), Mqtt: mqtt ? new("127.0.0.1", port) : null), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null, new(output.Version, "device", "Device", DeviceKind.Simulator), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Simulator: new(SampleJson: large ? "{\"value\":\"x\"}" : "{\"value\":1}")), "fixture", null);
        var config = mqtt ? JsonSerializer.SerializeToElement(new { connectionId = output.Connection.Id, topic, qos })
            : large ? JsonSerializer.SerializeToElement(new
            {
                connectionId = output.Connection.Id,
                path = "events",
                payload = new
                {
                    mode = "custom",
                    fields = new[] { new { target = "first", sourceField = "value" }, new { target = "second", sourceField = "value" } }
                }
            })
            : JsonSerializer.SerializeToElement(new { connectionId = output.Connection.Id, path = "events" });
        var graph = new RouteGraph(1, [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
            new("out", mqtt ? "output.mqtt" : "output.http", 1, "Output", config)], [new("in", "out")], new([]));
        return (new(input.Version, graph, input.Input.Id, large ? JsonSerializer.Serialize(new { value = new string('x', 50000) }) : "{\"value\":9223372036854775807}"), output.Connection.Id);
    }
    private static async Task<(Guid Id, JsonElement Output)> PreviewOutput(HttpClient client, RoutePreviewRequest request)
    {
        using var response = await client.PostJsonAsync(Preview, request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange((await response.Content.ReadAsByteArrayAsync(Token)).Length, 1, 65536);
        var json = await response.ReadJsonAsync(Token);
        Assert.True(json.GetProperty("previewed").GetBoolean());
        var output = json.GetProperty("result").GetProperty("paths")[0].GetProperty("output");
        return (output.GetProperty("testSendId").GetGuid(), output);
    }
    private static async Task<JsonElement> Review(HttpClient client, Guid id)
    {
        using var response = await client.PostJsonAsync(Prepare, new { id }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync(Token);
    }
    private static object Confirmation(JsonElement review) => new { id = review.GetProperty("id").GetGuid(), confirmationId = review.GetProperty("confirmationId").GetGuid(), confirmed = true };
    private static List<AuditLogRecord> Audits(IServiceProvider services, string action)
    {
        using var client = services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
        return client.Queryable<AuditLogRecord>().Where(row => row.Action == action).ToList();
    }
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    private static async Task<(string Headers, string Body)> ReadRequest(NetworkStream stream)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var one = new byte[1];
        var bytes = new List<byte>();
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, stop.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            {
                var headers = Encoding.ASCII.GetString(bytes.ToArray());
                var size = int.Parse(headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
                Assert.InRange(size, 1, StandardEnvelope.MaximumPayloadBytes);
                var body = new byte[size];
                await stream.ReadExactlyAsync(body, stop.Token);
                return (headers, Encoding.UTF8.GetString(body));
            }
        }
        throw new InvalidOperationException("fixture HTTP headers exceeded bound");
    }
}

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Credentials;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Adapters;

[Trait("Category", "RealBroker")]
public sealed class MqttClientCertificateRuntimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Theory]
    [InlineData("sharedInputOutput")]
    [InlineData("preview")]
    [InlineData("sample")]
    [InlineData("testSend")]
    [InlineData("connectionTest")]
    public async Task AllMqttPathsAuthenticateWithStoredCertificateDespiteOldAnonymousSnapshot(string path)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var factory = new TenonEdgeApplicationFactory();
        await MqttClientCertificateCredentialTests.ReadyAsync(factory);
        var store = MqttClientCertificateCredentialTests.Store(factory);
        var settings = fixture.Settings;
        await store.WriteAsync(1, new(settings.Recipient, Kind: MqttCredentialKind.ClientCertificate, Certificate: fixture.Issue()),
            "fixture", null, true, Token);
        var credential = store.ResolveMqtt(1, settings.Recipient)!;
        var connector = new CountingConnector();
        var topic = MqttProbe.NewTopic("product-mtls");
        var payload = "{\"counter\":9007199254740993}"u8.ToArray();
        var message = new OutputMessage(payload, topic, 1, settings.Fingerprint(credential.Identity), "fixture");
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(fixture.Broker, "independent-" + Guid.NewGuid().ToString("N")), Token);
        using var peerOwner = peer;
        var received = ReceivedMessages.Attach(peer);
        await MqttProbe.SubscribeAsync(peer, topic, Token);
        var fields = JsonSerializer.SerializeToElement(new[] { new { key = "counter", path = "/counter", type = "integer" } });
        var inputSettings = new MqttInputSettings
        { ConnectionId = 1, Host = settings.Host, Port = settings.Port, Tls = true, UsePassword = false, TopicFilter = topic, Fields = fields };
        if (path is "preview" or "sample")
        {
            await peer.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), Token);
        }
        switch (path)
        {
            case "sharedInputOutput":
                var captured = new TaskCompletionSource<MqttCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var receiver = new MqttInputReceiver([new(topic, 1, (value, identity, _) =>
                {
                    Assert.Equal(payload, value.Payload.ToArray());
                    captured.TrySetResult(identity);
                    return Task.FromResult(InputHandoffStatus.Committed);
                })], TimeProvider.System, reason => { if (reason == "acknowledged") { acknowledged.TrySetResult(); } },
                    bufferBudget: new InFlightBudget(64L * 1024 * 1024, 8L * 1024 * 1024));
                await using (var session = new MqttSession(settings, "shared-mtls-" + Guid.NewGuid().ToString("N"), store,
                    TimeProvider.System, connector, receiver, trust: fixture.Trust))
                {
                    using var deadline = MqttProbe.Timeout(Token);
                    Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
                    Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(message, deadline.Token)).Status);
                    Assert.Equal(credential, await captured.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
                    await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                    Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
                    Assert.Equal(1, connector.Connections);
                }
                break;
            case "preview":
            case "sample":
                var budget = new InFlightBudget(64L * 1024 * 1024, 8L * 1024 * 1024);
                var preview = new MqttReadPreview(new SharedConnections(), connector, store, new JsonInputDecoderFactory(),
                    budget, TimeProvider.System, fixture.Trust);
                if (path == "preview")
                {
                    var result = await preview.RunAsync(inputSettings, ClockSyncState.Synchronized, Token);
                    Assert.Equal("success", result.Code);
                    Assert.Equal("9007199254740993", Assert.Single(result.Preview!.Fields).Text);
                }
                else
                {
                    var result = await preview.ReadSampleAsync(inputSettings, "source", "device", ClockSyncState.Synchronized, Token);
                    Assert.Equal("success", result.Code);
                    Assert.Equal("9007199254740993", result.Message!.Values["counter"].ToString());
                }
                Assert.Equal(0, budget.Status.ReservedBytes);
                break;
            case "testSend":
                var sent = await new MqttTestMessageSender(connector, TimeProvider.System, fixture.Trust).SendAsync(settings, credential, message, Token);
                Assert.True(sent.BrokerAcknowledged);
                Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
                break;
            case "connectionTest":
                Assert.True((await new MqttConnectionTest(connector, TimeProvider.System, fixture.Trust).RunAsync(settings, credential, Token)).Success);
                break;
        }
        Assert.Equal(1, connector.Connections);
    }

    [Fact]
    public async Task RenewalRebuildsClientAndChangedAccountCannotReceiveOldBytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var factory = new TenonEdgeApplicationFactory();
        await MqttClientCertificateCredentialTests.ReadyAsync(factory);
        var store = MqttClientCertificateCredentialTests.Store(factory);
        var settings = fixture.Settings;
        var original = await SaveAsync(fixture.Issue(), null);
        var fingerprint = settings.Fingerprint(store.DescribeMqtt(1));
        var connector = new CountingConnector();
        await using var session = new MqttSession(settings, "rotation-mtls-" + Guid.NewGuid().ToString("N"), store,
            TimeProvider.System, connector, trust: fixture.Trust);
        var topic = MqttProbe.NewTopic("mtls-renewal");
        var payload = "{\"original\":9007199254740993}"u8.ToArray();
        var frozen = new OutputMessage(payload, topic, 1, fingerprint, "frozen");
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(fixture.Broker, "observer-" + Guid.NewGuid().ToString("N")), Token);
        using var peerOwner = peer;
        var received = ReceivedMessages.Attach(peer);
        await MqttProbe.SubscribeAsync(peer, topic, Token);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(frozen, deadline.Token)).Status);
        Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(frozen, deadline.Token)).Status);
        Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
        Assert.Equal(1, connector.Connections);
        var renewed = await SaveAsync(fixture.Issue(), original.Version);
        Assert.Equal(fingerprint, settings.Fingerprint(store.DescribeMqtt(1)));
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(frozen, deadline.Token)).Status);
        Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
        Assert.Equal(2, connector.Connections);
        var changed = await SaveAsync(fixture.Issue("another-account"), renewed.Version);
        var rejected = await session.SendAsync(frozen, deadline.Token);
        Assert.True(rejected.FingerprintMismatch);
        Assert.Equal(OutputSendStatus.SuspendTarget, rejected.Status);
        Assert.False(rejected.CountsTowardPoison);
        Assert.Equal(2, connector.Connections); // 未连接新账户，更没有向它偷发旧报文。
        var current = frozen with { Fingerprint = settings.Fingerprint(store.DescribeMqtt(1)) };
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(current, deadline.Token)).Status);
        Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
        Assert.Equal(3, connector.Connections);

        // 切换到密码后，要求证书的 Broker 必须拒绝；若旧私钥仍挂在客户端，会错误成功。
        var password = await store.WriteAsync(1, new(settings.Recipient, MosquittoBroker.UserName, MosquittoBroker.Password,
            ExpectedVersion: changed.Version), "fixture", null, true, Token);
        current = frozen with { Fingerprint = settings.Fingerprint(store.DescribeMqtt(1)) };
        Assert.NotEqual(OutputSendStatus.Ack, (await session.SendAsync(current, deadline.Token)).Status);
        Assert.False(session.IsConnected);
        await SaveAsync(fixture.Issue("another-account"), password.Version);
        current = frozen with { Fingerprint = settings.Fingerprint(store.DescribeMqtt(1)) };
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(current, deadline.Token)).Status);
        Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());

        Task<MqttCredentialMetadata> SaveAsync(ClientCertificateUpload upload, string? version) => store.WriteAsync(1,
            new(settings.Recipient, ExpectedVersion: version, Kind: MqttCredentialKind.ClientCertificate, Certificate: upload), "fixture", null, true, Token);
    }

    [Fact]
    public async Task ApiTemporaryAndStoredCertificateTestsPreserveDraftAndRequireIndependentServerTrust()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = fixture.Broker.TlsPort.ToString(CultureInfo.InvariantCulture) });
        using var api = await MqttClientCertificateCredentialTests.AdminAsync(factory);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = drafts.Write(null, new(drafts.Read().Version, "broker", "Broker", ConnectionKind.Mqtt,
            Mqtt: new("localhost", fixture.Broker.TlsPort, Tls: true)), "admin", null);
        var id = draft.Connection.Id;
        var request = new MqttConnectionTestRequest(new("localhost", fixture.Broker.TlsPort, Tls: true),
            ConnectionTestAuthentication.Provided, ConnectionId: id, Certificate: fixture.Issue());
        using var untrusted = await api.PostAsJsonAsync(new Uri("/api/edge-admin/v1/connections/test/mqtt", UriKind.Relative), request, Json, Token);
        Assert.Equal(HttpStatusCode.OK, untrusted.StatusCode);
        Assert.Equal("certificate.untrusted", (await untrusted.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token))!.Code);
        Assert.Null(MqttClientCertificateCredentialTests.Store(factory).Read(id));
        using var root = fixture.Broker.Authority.RootCertificate;
        var certificate = ServerCertificateTrust.Describe(root)!;
        factory.Services.GetRequiredService<ConnectionTrustStore>().Write(id, new(draft.Version, fixture.Settings.Recipient,
            TlsTrustMode.CertificateAuthority, certificate.CertificateDerBase64, certificate.Sha256Fingerprint, true), Actor());
        using var provided = await api.PostAsJsonAsync(new Uri("/api/edge-admin/v1/connections/test/mqtt", UriKind.Relative), request, Json, Token);
        Assert.Equal(HttpStatusCode.OK, provided.StatusCode);
        Assert.True((await provided.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token))!.Success);
        Assert.Null(MqttClientCertificateCredentialTests.Store(factory).Read(id));
        await MqttClientCertificateCredentialTests.Store(factory).WriteAsync(id, new(fixture.Settings.Recipient,
            ExpectedDraftVersion: draft.Version, Kind: MqttCredentialKind.ClientCertificate, Certificate: fixture.Issue()),
            "admin", null, true, Token);
        using var stored = await api.PostAsJsonAsync(new Uri("/api/edge-admin/v1/connections/test/mqtt", UriKind.Relative),
            request with { Authentication = ConnectionTestAuthentication.Stored, Certificate = null, CredentialConnectionId = id }, Json, Token);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.True((await stored.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token))!.Success);
        Assert.Equal(draft.Version, drafts.Read().Version);
        using var mixed = await api.PostAsJsonAsync(new Uri("/api/edge-admin/v1/connections/test/mqtt", UriKind.Relative),
            request with { Password = "fixture-secret" }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.DoesNotContain("fixture-secret", await mixed.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // 原路线二次确认服务：公开核对证书主题；准备后换证即使同主题也须重新确认。
        var artifacts = factory.Services.GetRequiredService<RouteTestSendArtifacts>();
        var service = factory.Services.GetRequiredService<RouteTestSendService>();
        var topic = MqttProbe.NewTopic("certificate-test-send");
        var payload = "{\"value\":9007199254740993}"u8.ToArray();
        var firstReview = Review();
        Assert.Equal("clientcertificate", firstReview.Authentication);
        Assert.Equal("CN=gateway-client", firstReview.Username);
        var credentialStore = MqttClientCertificateCredentialTests.Store(factory);
        await credentialStore.WriteAsync(id, new(fixture.Settings.Recipient, ExpectedDraftVersion: draft.Version,
            ExpectedVersion: credentialStore.Read(id)!.Version, Kind: MqttCredentialKind.ClientCertificate, Certificate: fixture.Issue()),
            "admin", null, true, Token);
        Assert.Equal("testSend.credentialChanged", (await Assert.ThrowsAsync<RouteTestSendException>(() =>
            service.ConfirmAsync(firstReview.Id, firstReview.ConfirmationId, true, "fixture-owner", Actor(), Token))).Code);
        Assert.Empty(TestAccounts.Audit(factory.Services, "route.testSend.start"));
        var review = Review();
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(fixture.Broker, "test-send-observer-" + Guid.NewGuid().ToString("N")), Token);
        using var peerOwner = peer;
        var messages = ReceivedMessages.Attach(peer);
        await MqttProbe.SubscribeAsync(peer, topic, Token);
        var sent = await service.ConfirmAsync(review.Id, review.ConfirmationId, true, "fixture-owner", Actor(), Token);
        Assert.True(sent.Mqtt!.BrokerAcknowledged);
        Assert.Equal(payload, (await messages.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
        Assert.Single(TestAccounts.Audit(factory.Services, "route.testSend.start"));
        Assert.Single(TestAccounts.Audit(factory.Services, "route.testSend.complete"));
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Equal(draft.Version, drafts.Read().Version);
        Assert.Equal(404, (await Assert.ThrowsAsync<RouteTestSendException>(() =>
            service.ConfirmAsync(review.Id, review.ConfirmationId, true, "fixture-owner", Actor(), Token))).Status);

        RouteTestSendReview Review()
        {
            var capture = new RouteTestSendCapture();
            var artifactId = Guid.NewGuid();
            var settings = new MqttOutputSettings
            {
                ConnectionId = id,
                Host = "localhost",
                Port = fixture.Broker.TlsPort,
                Tls = true,
                Topic = topic,
                Inputs = ["device/source"],
                UsePassword = false
            };
            Assert.True(capture.TryAdd(artifactId, draft.Version, 1, Guid.NewGuid(), DateTimeOffset.UtcNow, ["in", "out"],
                JsonSerializer.SerializeToElement(settings, Json), new("out", "output.mqtt", Guid.NewGuid(), topic, 1,
                    new Dictionary<string, string>(), "", payload.Length, false, artifactId), payload));
            Assert.True(artifacts.Publish("fixture-owner", capture));
            return service.Prepare(artifactId, "fixture-owner", Token);
        }

        TenonEdge.Host.Security.ManagementActor Actor()
        {
            var account = TestAccounts.Find(factory.Services, "admin")!;
            return new(account.Id, account.SecurityStamp, null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductProcessReloadsProtectedIdentityAndPreservesOrIsolatesFrozenQueue(bool changedAccount)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var data = TemporaryDirectory.Create();
        using var work = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("restart-mtls");
        var payload = "{\"counter\":9007199254740993}"u8.ToArray();
        var deliveryId = Guid.NewGuid();
        long id;
        string originalFingerprint;
        string privateMaterial;
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var api = await MqttClientCertificateCredentialTests.AdminAsync(bootstrap);
            var drafts = bootstrap.Services.GetRequiredService<ConnectionDraftStore>();
            var draft = drafts.Write(null, new(drafts.Read().Version, "broker", "Broker", ConnectionKind.Mqtt,
                Mqtt: new("localhost", fixture.Broker.TlsPort, Tls: true)), "admin", null);
            id = draft.Connection.Id;
            using var root = fixture.Broker.Authority.RootCertificate;
            var info = ServerCertificateTrust.Describe(root)!;
            using var saved = await api.PutAsJsonAsync(new Uri($"/api/edge-admin/v1/connections/{id}/tls-trust", UriKind.Relative),
                new ConnectionTrustRequest(draft.Version, fixture.Settings.Recipient, TlsTrustMode.CertificateAuthority,
                    info.CertificateDerBase64, info.Sha256Fingerprint, true), Json, Token);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var store = MqttClientCertificateCredentialTests.Store(bootstrap);
            var first = await store.WriteAsync(id, new(fixture.Settings.Recipient, ExpectedDraftVersion: draft.Version,
                Kind: MqttCredentialKind.ClientCertificate, Certificate: fixture.Issue()), "admin", null, true, Token);
            var settings = fixture.Settings with { ConnectionId = id };
            originalFingerprint = settings.Fingerprint(store.DescribeMqtt(id));
            var cache = bootstrap.Services.GetRequiredService<DeliveryCache>();
            Assert.True((await cache.EnqueueAsync([Draft(deliveryId)], Token)).Committed);
            await store.WriteAsync(id, new(settings.Recipient, ExpectedDraftVersion: draft.Version, ExpectedVersion: first.Version,
                AccountChanged: changedAccount ? null : false, Kind: MqttCredentialKind.ClientCertificate,
                Certificate: fixture.Issue(changedAccount ? "other" : "gateway-client")), "admin", null, true, Token);
            Assert.True((await cache.EnqueueAsync([Draft(Guid.NewGuid())], Token)).Committed);
            var plan = RoutePlanCompiler.Compile(bootstrap.Services.GetRequiredService<RouteDraftStore>().ReadCompilation(draft.Version, Token), Token).Document;
            var versions = bootstrap.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(plan, draft.Version, "admin"), null);
            privateMaterial = store.ResolveMqtt(id, settings.Recipient)!.Password;
            Assert.DoesNotContain(privateMaterial, versions.Load(1)!.Content, StringComparison.Ordinal);
            DeliveryDraft Draft(Guid delivery) => new(delivery, id, Guid.NewGuid(), "device", 1, payload,
                settings.Fingerprint(store.DescribeMqtt(id)), OutputRecipientIdentity.Mqtt(settings.Recipient, false, store.DescribeMqtt(id)).Freeze(),
                topic, 1);
        }
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(fixture.Broker, "restart-observer-" + Guid.NewGuid().ToString("N")), Token);
        using var peerOwner = peer;
        var received = ReceivedMessages.Attach(peer);
        await MqttProbe.SubscribeAsync(peer, topic, Token);
        await using (var host = HostProcess.Start(work.Path, "Production", data.Path,
            additionalArguments: [$"--TenonEdge:Outbound:LocalPorts:0={fixture.Broker.TlsPort.ToString(CultureInfo.InvariantCulture)}"]))
        {
            using var client = new HttpClient { BaseAddress = await host.WaitForListeningAddressAsync(TimeSpan.FromSeconds(20), Token) };
            await host.WaitUntilReadyAsync(client, TimeSpan.FromSeconds(20), Token);
            for (var index = 0; index < (changedAccount ? 1 : 2); index++)
            { Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray()); }
            await OutputPipelineTests.UntilAsync(() =>
            {
                using var db = OpenQueue();
                using var query = db.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM delivery WHERE state='pending'";
                return Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
            });
            await host.RequestStopAsync(Token);
            Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(20), Token));
            Assert.DoesNotContain(privateMaterial, host.Output, StringComparison.Ordinal);
        }
        using var database = OpenQueue();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT delivery_id, fingerprint, payload, state FROM delivery";
        using var reader = command.ExecuteReader();
        if (changedAccount)
        {
            Assert.True(reader.Read());
            Assert.Equal(deliveryId, Guid.Parse(reader.GetString(0)));
            Assert.Equal(originalFingerprint, reader.GetString(1));
            Assert.Equal(payload, (byte[])reader.GetValue(2));
            Assert.Equal("quarantined", reader.GetString(3));
        }
        Assert.False(reader.Read());

        SqliteConnection OpenQueue()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Mode=ReadOnly;Pooling=False");
            connection.Open();
            return connection;
        }
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("damaged")]
    [InlineData("wrongSubject")]
    public async Task InvalidCertificateSuspendsBeforeNetworkAndDoesNotCountAsPoison(string scenario)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "client", clientOnly: true, expired: scenario == "expired");
        var bytes = certificate.Export(X509ContentType.Pfx, string.Empty);
        var credential = new MqttCredential("", scenario == "damaged" ? "damaged-material" : Convert.ToBase64String(bytes),
            "a", "v1", MqttCredentialKind.ClientCertificate, scenario == "wrongSubject" ? "CN=wrong" : certificate.Subject);
        CryptographicOperations.ZeroMemory(bytes);
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", Port = 8883, Tls = true };
        var connector = new CountingConnector(forbid: true);
        await using var session = new MqttSession(settings, "invalid", new Resolver(credential), TimeProvider.System, connector);
        var result = await session.SendAsync(new("{}"u8.ToArray(), "topic", 1, settings.Fingerprint(credential.Identity), "fixture"), Token);
        Assert.Equal(OutputSendStatus.SuspendTarget, result.Status);
        Assert.False(result.CountsTowardPoison);
        Assert.Equal(0, connector.Connections);
        Assert.DoesNotContain(credential.Password, result.ToString(), StringComparison.Ordinal);
        var preview = new MqttReadPreview(new SharedConnections(), connector, new Resolver(credential), new JsonInputDecoderFactory(),
            new InFlightBudget(64L * 1024 * 1024, 8L * 1024 * 1024), TimeProvider.System);
        var sample = await preview.RunAsync(new MqttInputSettings
        {
            ConnectionId = 1,
            Host = "localhost",
            Tls = true,
            Port = 8883,
            TopicFilter = "topic",
            Fields = JsonSerializer.SerializeToElement(Array.Empty<object>())
        }, ClockSyncState.Unknown, Token);
        Assert.Equal("credential.unavailable", sample.Code);
        await Assert.ThrowsAsync<ArgumentException>(() => new MqttConnectionTest(connector, TimeProvider.System).RunAsync(settings, credential, Token));
        Assert.Equal(0, connector.Connections);
    }

    private sealed class Resolver(MqttCredential value) : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => value.Identity;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => value;
    }

    private sealed class CountingConnector(bool forbid = false) : IOutboundConnector
    {
        private readonly OutboundConnector _inner = new(new ConnectionTrustTests.LoopbackPolicy());
        internal int Connections;
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Connections);
            if (forbid)
            { throw new InvalidOperationException("No network expected"); }
            return _inner.ConnectAsync(host, port, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ECDsa _key = ECDsa.Create();
        private readonly X509Certificate2 _root;
        internal MosquittoBroker Broker { get; private set; } = null!;
        private Fixture() { _root = ConnectionTrustTests.Certificate(_key, "mqtt-client-ca", authority: true); }
        internal MqttConnectionSettings Settings => new() { ConnectionId = 1, Host = "localhost", Port = Broker.TlsPort, Tls = true, UsePassword = false };
        internal ITlsTrustResolver Trust
        {
            get
            {
                using var root = Broker.Authority.RootCertificate;
                return new ConnectionTrustTests.TrustResolver(1, Settings.Recipient,
                    new(TlsTrustMode.CertificateAuthority, Convert.ToBase64String(root.RawData), "fixture-trust"));
            }
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? "");
            var fixture = new Fixture();
            try
            { fixture.Broker = await MosquittoBroker.StartAsync(Token, clientAuthorityPem: fixture._root.ExportCertificatePem()); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal ClientCertificateUpload Issue(string name = "gateway-client")
        {
            using var key = ECDsa.Create();
            using var certificate = ConnectionTrustTests.Certificate(key, name, issuer: _root, clientOnly: true);
            return new(CertificatePem: certificate.ExportCertificatePem() + "\n" + _root.ExportCertificatePem(), PrivateKeyPem: key.ExportPkcs8PrivateKeyPem());
        }
        public async ValueTask DisposeAsync()
        {
            if (Broker is not null)
            { await Broker.DisposeAsync(); }
            _root.Dispose();
            _key.Dispose();
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Credentials;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class HttpClientCertificateRuntimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Theory]
    [InlineData("input")]
    [InlineData("diagnostic")]
    [InlineData("preview")]
    [InlineData("sample")]
    [InlineData("output")]
    [InlineData("testSend")]
    [InlineData("connectionTest")]
    public async Task AllHttpPathsUseStoredPrivateIdentityWithoutSendingItAsHttpContent(string path)
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        await using var factory = new TenonEdgeApplicationFactory();
        await HttpClientCertificateCredentialTests.ReadyAsync(factory);
        var store = HttpClientCertificateCredentialTests.Store(factory);
        await store.WriteAsync(1, new(peer.Recipient, HttpCredentialKind.ClientCertificate, Certificate: peer.Issue()), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var credential = store.ResolveHttp(1, peer.Recipient)!;
        var connector = new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy());
        var trust = new ConnectionTrustTests.TrustResolver(1, peer.Recipient, peer.Trust);
        await using var session = new HttpConnectionSession(connector, 5000, trust);
        var fields = JsonSerializer.SerializeToElement(new[] { new { key = "counter", path = "/counter", type = "integer" } });
        var settings = new HttpInputSettings { ConnectionId = 1, BaseAddress = peer.Recipient, Fields = fields };
        var decoder = new JsonInputDecoderFactory().Build(fields);
        var output = new HttpOutputSettings { ConnectionId = 1, BaseAddress = peer.Recipient };
        var message = output.FreezePayload("{\"value\":9007199254740993}"u8.ToArray(), Guid.NewGuid(), credential.Identity);
        switch (path)
        {
            case "input":
                using (var document = await session.ReadInputAsync(settings, store, Token))
                { Assert.Equal(9007199254740993, document.RootElement.GetProperty("counter").GetInt64()); }
                break;
            case "diagnostic":
                var diagnostic = await session.ReadDiagnosticInputAsync(settings, store, Token);
                using (diagnostic.Document)
                { Assert.Equal(DiagnosticSampleAccess.Safe, diagnostic.Access); }
                break;
            case "preview":
                var preview = await session.ReadPreviewAsync(settings, store, decoder, ClockSyncState.Synchronized, TimeProvider.System, 65536, Token);
                Assert.True(preview.CanUseAsSample);
                Assert.Equal("9007199254740993", Assert.Single(preview.Fields).Text);
                break;
            case "sample":
                var sample = await session.ReadSampleAsync(settings, store, decoder, "source", "device", ClockSyncState.Synchronized, TimeProvider.System, Token);
                Assert.Equal("success", sample.Code);
                Assert.Equal("9007199254740993", sample.Message!.Values["counter"].ToString());
                break;
            case "output":
                Assert.Equal(OutputSendStatus.Ack, (await session.SendOutputAsync(output, store, TimeProvider.System, message, Token)).Status);
                break;
            case "testSend":
                var sender = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System, trust);
                Assert.Equal(200, (await sender.SendAsync(output, credential, message, Token)).StatusCode);
                break;
            case "connectionTest":
                var probe = new HttpConnectionTest(new SharedConnections(), connector, TimeProvider.System, trust);
                Assert.True((await probe.RunAsync(settings, credential, Token)).Success);
                break;
        }
        var received = Assert.Single(peer.Requests);
        Assert.Equal("CN=gateway-client", received.Subject);
        Assert.Empty(received.Authorization);
        if (path is "output" or "testSend")
        { Assert.Equal(message.Payload.ToArray(), received.Body); }
        else
        { Assert.Empty(received.Body); }
    }

    [Fact]
    public async Task RotationRebuildsSharedClientAndOtherAccountCannotReceiveOldFrozenMessage()
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(false, Token);
        await using var factory = new TenonEdgeApplicationFactory();
        await HttpClientCertificateCredentialTests.ReadyAsync(factory);
        var store = HttpClientCertificateCredentialTests.Store(factory);
        var first = await store.WriteAsync(1, new(peer.Recipient, HttpCredentialKind.ClientCertificate, Certificate: peer.Issue()), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var settings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = peer.Recipient };
        var oldIdentity = store.DescribeHttp(1)!;
        var frozen = settings.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), oldIdentity);
        await using var session = new HttpConnectionSession(new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy()), 5000,
            new ConnectionTrustTests.TrustResolver(1, peer.Recipient, peer.Trust));
        Assert.Equal(OutputSendStatus.Ack, (await SendAsync(frozen)).Status);
        Assert.Equal(OutputSendStatus.Ack, (await SendAsync(frozen)).Status);
        var requests = peer.Requests.ToArray();
        Assert.Equal(requests[0].Connection, requests[1].Connection); // 确实复用旧连接，不以每次都重连冒充轮换。
        var same = await store.WriteAsync(1, new(peer.Recipient, HttpCredentialKind.ClientCertificate, ExpectedVersion: first.Version,
            Certificate: peer.Issue()), "fixture", null, true, token: TestContext.Current.CancellationToken);
        Assert.Equal(settings.Fingerprint(oldIdentity), settings.Fingerprint(store.DescribeHttp(1)));
        Assert.Equal(OutputSendStatus.Ack, (await SendAsync(frozen)).Status);
        var renewed = peer.Requests.Last();
        Assert.NotEqual(requests[0].Connection, renewed.Connection);
        Assert.NotEqual(requests[0].Fingerprint, renewed.Fingerprint);
        var changed = await store.WriteAsync(1, new(peer.Recipient, HttpCredentialKind.ClientCertificate, ExpectedVersion: same.Version,
            Certificate: peer.Issue("other-account")), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var blocked = await SendAsync(frozen);
        Assert.Equal(OutputSendStatus.SuspendTarget, blocked.Status);
        Assert.True(blocked.FingerprintMismatch);
        Assert.Equal(3, peer.Requests.Count);
        var current = settings.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), store.DescribeHttp(1));
        Assert.Equal(OutputSendStatus.Ack, (await SendAsync(current)).Status);
        Assert.Equal("CN=other-account", peer.Requests.Last().Subject);
        await store.WriteAsync(1, new(peer.Recipient, HttpCredentialKind.Bearer, Secret: "fixture-token", ExpectedVersion: changed.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        current = settings.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), store.DescribeHttp(1));
        Assert.Equal(OutputSendStatus.Ack, (await SendAsync(current)).Status);
        Assert.Null(peer.Requests.Last().Subject); // 切回 Token 不继续携带旧客户端私钥。
        Assert.Equal("Bearer fixture-token", peer.Requests.Last().Authorization);
        Task<OutputSendResult> SendAsync(OutputMessage message) => session.SendOutputAsync(settings, store, TimeProvider.System, message, Token);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("damaged")]
    [InlineData("wrongSubject")]
    public async Task InvalidStoredPrivateMaterialSuspendsBeforeNetworkAndIsNotPoison(string scenario)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "client", clientOnly: true, expired: scenario == "expired");
        var bytes = certificate.Export(X509ContentType.Pfx, string.Empty);
        var credential = new HttpCredential(new(HttpCredentialKind.ClientCertificate, null, null, "a", "v1",
            scenario == "wrongSubject" ? "CN=wrong" : certificate.Subject), scenario == "damaged" ? "fixture-damaged" : Convert.ToBase64String(bytes));
        CryptographicOperations.ZeroMemory(bytes);
        var settings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = "https://localhost:8883/" };
        var message = settings.FreezePayload("{}"u8.ToArray(), Guid.NewGuid(), credential.Identity);
        var connector = new NoConnect();
        await using var session = new HttpConnectionSession(connector, 5000);
        var result = await session.SendOutputAsync(settings, new FixedCredential(credential), TimeProvider.System, message, Token);
        Assert.Equal(OutputSendStatus.SuspendTarget, result.Status);
        Assert.False(result.CountsTowardPoison);
        Assert.Equal(0, connector.Calls);
    }

    private sealed class NoConnect : IOutboundConnector
    {
        public int Calls { get; private set; }
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("invalid material must not connect"); }
    }
    private sealed class FixedCredential(HttpCredential value) : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => value.Identity;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => value;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHostRestartsWithProtectedCertificateAndIsolatesPreviousAccountWithoutRewritingBytes(bool differentSubject)
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        using var data = TemporaryDirectory.Create();
        long id;
        byte[] original = "{\"counter\":9007199254740993}"u8.ToArray();
        Guid deliveryId = Guid.NewGuid();
        string oldFingerprint;
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = await HttpClientCertificateCredentialTests.AdminAsync(bootstrap);
            var drafts = bootstrap.Services.GetRequiredService<ConnectionDraftStore>();
            var draft = drafts.Write(null, new(drafts.Read().Version, "target", "Target", ConnectionKind.Http, Http: new(peer.Recipient)), "admin", null);
            id = draft.Connection.Id;
            var info = ServerCertificateTrust.Validate(peer.Trust);
            using var saved = await client.PutAsJsonAsync(new Uri($"/api/edge-admin/v1/connections/{id}/tls-trust", UriKind.Relative),
                new ConnectionTrustRequest(draft.Version, peer.Recipient, peer.Trust.Mode, peer.Trust.CertificateDerBase64, info.Sha256Fingerprint, true), Json, Token);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var store = HttpClientCertificateCredentialTests.Store(bootstrap);
            var first = await store.WriteAsync(id, new(peer.Recipient, HttpCredentialKind.ClientCertificate, ExpectedDraftVersion: draft.Version,
                Certificate: peer.Issue()), "admin", null, true, token: TestContext.Current.CancellationToken);
            var settings = new HttpOutputSettings { ConnectionId = id, BaseAddress = peer.Recipient, Path = "events" };
            var message = settings.FreezePayload(original, deliveryId, store.DescribeHttp(id));
            oldFingerprint = message.Fingerprint;
            Assert.Equal(id, Draft(message, deliveryId).ConnectionId);
            var cache = bootstrap.Services.GetRequiredService<DeliveryCache>();
            Assert.True((await cache.EnqueueAsync([Draft(message, deliveryId)], Token)).Committed);
            await store.WriteAsync(id, new(peer.Recipient, HttpCredentialKind.ClientCertificate, ExpectedVersion: first.Version, ExpectedDraftVersion: draft.Version,
                AccountChanged: differentSubject ? null : false, Certificate: peer.Issue(differentSubject ? "other-account" : "gateway-client")), "admin", null, true, token: TestContext.Current.CancellationToken);
            var newId = Guid.NewGuid();
            var current = settings.FreezePayload(original, newId, store.DescribeHttp(id));
            Assert.True((await cache.EnqueueAsync([Draft(current, newId)], Token)).Committed);
            var routes = bootstrap.Services.GetRequiredService<RouteDraftStore>();
            var plan = RoutePlanCompiler.Compile(routes.ReadCompilation(draft.Version, Token), Token).Document;
            var versions = bootstrap.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(plan, draft.Version, "admin"), null);
            Assert.DoesNotContain(store.ResolveHttp(id, peer.Recipient)!.Secret, versions.Load(1)!.Content, StringComparison.Ordinal);
            Assert.Empty(peer.Requests); // 保存凭证/草稿/冻结队列本身不发送。
            DeliveryDraft Draft(OutputMessage value, Guid delivery) => new(deliveryId: delivery, connectionId: id, sourceMessageId: Guid.NewGuid(), deviceKey: "device", revision: 1,
                value.Payload, value.Fingerprint, value.FingerprintSummary, value.Address, null, value.Headers, protocolMetadata: value.ProtocolMetadata);
        }
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = new Uri(peer.Recipient).Port.ToString(CultureInfo.InvariantCulture) }))
        {
            using var started = host.CreateClient();
            var cache = host.Services.GetRequiredService<DeliveryCache>();
            var expected = differentSubject ? 1 : 2;
            await OutputPipelineTests.UntilAsync(() => cache.GetStatus().DeletedRecords == expected);
            Assert.Equal(expected, peer.Requests.Count);
            Assert.All(peer.Requests, request =>
            {
                Assert.Equal(differentSubject ? "CN=other-account" : "CN=gateway-client", request.Subject);
                Assert.Equal(original, request.Body);
                Assert.Empty(request.Authorization);
            });
            Assert.Empty(cache.ReadPending(id, 0, 10));
            if (differentSubject)
            {
                var isolated = Assert.Single(cache.ReadQuarantined(id, 0, 10));
                Assert.Equal(deliveryId, isolated.DeliveryId);
                Assert.Equal(oldFingerprint, isolated.Fingerprint);
                Assert.Equal(original, isolated.Payload.ToArray());
            }
            else
            { Assert.Empty(cache.ReadQuarantined(id, 0, 10)); }
            Assert.Equal(1, host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredAndUnsavedCertificateConnectionTestsUseOriginalApiAndDoNotSaveTestMaterial(bool stored)
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = new Uri(peer.Recipient).Port.ToString(CultureInfo.InvariantCulture) });
        using var client = await HttpClientCertificateCredentialTests.AdminAsync(factory);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = drafts.Write(null, new(drafts.Read().Version, "tls", "TLS", ConnectionKind.Http, Http: new(peer.Recipient)), "admin", null);
        var id = draft.Connection.Id;
        var info = ServerCertificateTrust.Validate(peer.Trust);
        using var savedTrust = await client.PutAsJsonAsync(new Uri($"/api/edge-admin/v1/connections/{id}/tls-trust", UriKind.Relative),
            new ConnectionTrustRequest(draft.Version, peer.Recipient, peer.Trust.Mode, peer.Trust.CertificateDerBase64, info.Sha256Fingerprint, true), Json, Token);
        Assert.Equal(HttpStatusCode.OK, savedTrust.StatusCode);
        var upload = peer.Issue(pfx: stored);
        var store = HttpClientCertificateCredentialTests.Store(factory);
        if (stored)
        { await store.WriteAsync(id, new(peer.Recipient, HttpCredentialKind.ClientCertificate, ExpectedDraftVersion: draft.Version, Certificate: upload), "admin", null, true, token: TestContext.Current.CancellationToken); }
        using var response = await client.PostAsJsonAsync(new Uri("/api/edge-admin/v1/connections/test/http", UriKind.Relative),
            new HttpConnectionTestRequest(new(peer.Recipient), id, stored ? ConnectionTestAuthentication.Stored : ConnectionTestAuthentication.Provided,
                stored ? null : new(HttpCredentialKind.ClientCertificate, Certificate: upload)), Json, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<HttpConnectionTestResult>(Token);
        Assert.True(result!.Success);
        Assert.Equal("CN=gateway-client", Assert.Single(peer.Requests).Subject);
        Assert.Equal(stored, store.Read(id) is not null);
        Assert.Equal(draft.Version, drafts.Read().Version);
    }
}

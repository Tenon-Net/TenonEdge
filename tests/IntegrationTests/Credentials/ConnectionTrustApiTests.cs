using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Adapters;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class ConnectionTrustApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private const string Recipient = "https://localhost:8883/";
    private static string Path(long id) => $"/api/edge-admin/v1/connections/{id}/tls-trust";

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task CertificateInspectionIsAdministratorOnlyAndDoesNotSaveTrust(string? role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        if (role is not null)
        { TestAccounts.Create(factory.Services, "actor", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role)); }
        using var client = role is null ? factory.CreateClient() : await factory.CreateLoggedInClientAsync("actor", Token);
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "root", authority: true);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/connections/tls-trust/inspect",
            new { certificateDerBase64 = Convert.ToBase64String(certificate.RawData) }, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True(response.Headers.CacheControl?.NoStore);
            var info = await response.Content.ReadFromJsonAsync<ServerCertificateInfo>(Token);
            Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), info!.Sha256Fingerprint);
            using var db = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
            Assert.Equal(0, db.Queryable<ConnectionTrustRecord>().Count());
            client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
            using var noCsrf = await client.PostJsonAsync("/api/edge-admin/v1/connections/tls-trust/inspect",
                new { certificateDerBase64 = info.CertificateDerBase64 }, Token);
            Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        }
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("invalid")]
    [InlineData("private")]
    [InlineData("multiple")]
    [InlineData("large")]
    public async Task InspectionRejectsInvalidOrNonCaCertificates(string scenario)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "actor", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("actor", Token);
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "fixture", authority: scenario != "leaf",
            expired: scenario == "expired", future: scenario == "future");
        var encoded = scenario switch
        {
            "invalid" => "invalid",
            "private" => Convert.ToBase64String(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx)),
            "multiple" => Convert.ToBase64String([.. certificate.RawData, .. certificate.RawData]),
            "large" => new string('A', 45000),
            _ => Convert.ToBase64String(certificate.RawData),
        };
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/connections/tls-trust/inspect",
            new { certificateDerBase64 = encoded }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK, HttpStatusCode.NotFound)]
    [InlineData("Configurator", HttpStatusCode.Forbidden, HttpStatusCode.NotFound)]
    [InlineData("Viewer", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public async Task OnlyAdministratorCanConfirmTrustAndMetadataExcludesCertificate(string role, HttpStatusCode expected, HttpStatusCode read)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "actor", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("actor", Token);
        var draft = Draft(factory.Services);
        using var absent = await client.GetAsync(Path(draft.Connection.Id), Token);
        Assert.Equal(read, absent.StatusCode);
        var request = Request(draft.Version);
        using var response = await Put(client, draft.Connection.Id, request);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK)
        { return; }
        var saved = await response.Content.ReadFromJsonAsync<ConnectionTrustMetadata>(Json, Token);
        Assert.NotNull(saved);
        Assert.Equal(request.ConfirmedFingerprint, saved.Sha256Fingerprint);
        Assert.True(saved.Available);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(request.CertificateDerBase64, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        var store = factory.Services.GetRequiredService<ConnectionTrustStore>();
        Assert.Equal(request.CertificateDerBase64, store.ResolveTlsTrust(draft.Connection.Id, Recipient)!.CertificateDerBase64);
        Assert.Null(store.ResolveTlsTrust(draft.Connection.Id + 1, Recipient));
        Assert.Null(store.ResolveTlsTrust(draft.Connection.Id, Recipient + "other/"));
        Assert.Equal(draft.Version, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        using var database = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
        var row = database.Queryable<ConnectionTrustRecord>().InSingle(draft.Connection.Id);
        Assert.DoesNotContain(request.CertificateDerBase64, row.ProtectedCertificate, StringComparison.Ordinal);
        var audit = Assert.Single(TestAccounts.Audit(factory.Services, "connection.trust.write"));
        Assert.Contains(request.ConfirmedFingerprint, audit.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(request.CertificateDerBase64, audit.Detail, StringComparison.Ordinal);
        using var get = await client.GetAsync(Path(draft.Connection.Id), Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.DoesNotContain(request.CertificateDerBase64, await get.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unconfirmed", HttpStatusCode.BadRequest)]
    [InlineData("fingerprint", HttpStatusCode.BadRequest)]
    [InlineData("corrupt", HttpStatusCode.BadRequest)]
    [InlineData("oversize", HttpStatusCode.BadRequest)]
    [InlineData("leafAsCa", HttpStatusCode.BadRequest)]
    [InlineData("draft", HttpStatusCode.Conflict)]
    [InlineData("recipient", HttpStatusCode.Conflict)]
    [InlineData("version", HttpStatusCode.Conflict)]
    public async Task InvalidOrStaleConfirmationsDoNotWriteOrAudit(string scenario, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var draft = Draft(factory.Services);
        var request = Request(draft.Version);
        request = scenario switch
        {
            "unconfirmed" => request with { Confirmed = false },
            "fingerprint" => request with { ConfirmedFingerprint = "wrong" },
            "corrupt" => request with { CertificateDerBase64 = "broken" },
            "oversize" => request with { CertificateDerBase64 = new string('A', 45000) },
            "leafAsCa" => request with { Mode = TlsTrustMode.CertificateAuthority },
            "draft" => request with { ExpectedDraftVersion = "stale" },
            "recipient" => request with { Recipient = "https://other.invalid/" },
            _ => request with { ExpectedVersion = "stale" },
        };
        using var response = await Put(client, draft.Connection.Id, request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<ConnectionTrustStore>().Read(draft.Connection.Id));
        Assert.Empty(TestAccounts.Audit(factory.Services, "connection.trust.write"));
    }

    [Fact]
    public async Task CsrfAnonymousAndIdentityChangedInsideTransactionCannotWrite()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var admin = TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var draft = Draft(factory.Services);
        var request = Request(draft.Version);
        using var anonymous = factory.CreateClient();
        using var denied = await Put(anonymous, draft.Connection.Id, request);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await Put(client, draft.Connection.Id, request);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        var actor = new ManagementActor(admin.Id, admin.SecurityStamp, "fixture");
        TestAccounts.Update(factory.Services, "admin", user => user.Role = nameof(UserRole.Configurator));
        Assert.Throws<ApiTokenException>(() => factory.Services.GetRequiredService<ConnectionTrustStore>().Write(draft.Connection.Id, request, actor));
        Assert.Empty(TestAccounts.Audit(factory.Services, "connection.trust.write"));
    }

    [Fact]
    public async Task RestartKeepsProtectedTrustRotationIsVersionedAndCorruptionCannotBecomeSystemTrust()
    {
        using var directory = TemporaryDirectory.Create();
        long id;
        string draftVersion;
        ConnectionTrustMetadata first;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var client = await factory.CreateLoggedInClientAsync("admin", Token);
            var draft = Draft(factory.Services);
            (id, draftVersion) = (draft.Connection.Id, draft.Version);
            using var saved = await Put(client, id, Request(draftVersion));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            first = (await saved.Content.ReadFromJsonAsync<ConnectionTrustMetadata>(Json, Token))!;
        }
        await using (var restarted = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            var store = restarted.Services.GetRequiredService<ConnectionTrustStore>();
            Assert.Equal(first, store.Read(id));
            using var client = await restarted.CreateLoggedInClientAsync("admin", Token);
            var rotated = Request(draftVersion) with { ExpectedVersion = first.Version };
            using var saved = await Put(client, id, rotated);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var stale = await Put(client, id, rotated);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var next = store.ResolveTlsTrust(id, Recipient)!;
            Assert.NotEqual(first.Version, next.Version);
            using var database = restarted.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
            var row = database.Queryable<ConnectionTrustRecord>().InSingle(id);
            row.ProtectedCertificate = "damaged";
            database.Updateable(row).ExecuteCommand();
            Assert.False(store.Read(id)!.Available);
            var damaged = store.ResolveTlsTrust(id, Recipient);
            Assert.NotNull(damaged);
            Assert.Equal("", damaged.CertificateDerBase64);
            using var clear = await client.PostJsonAsync(Path(id) + "/clear", new ConnectionTrustClearRequest(draftVersion, Recipient, next.Version, true), Token);
            Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
            Assert.Null(store.Read(id));
            Assert.Null(store.ResolveTlsTrust(id, Recipient));
            Assert.Equal(2, TestAccounts.Audit(restarted.Services, "connection.trust.write").Count);
            Assert.Single(TestAccounts.Audit(restarted.Services, "connection.trust.clear"));
        }
    }

    [Fact]
    public async Task DeletingAnUnreferencedDraftAlsoDeletesItsTrust()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var account = TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var draft = Draft(factory.Services);
        var store = factory.Services.GetRequiredService<ConnectionTrustStore>();
        _ = store.Write(draft.Connection.Id, Request(draft.Version), new(account.Id, account.SecurityStamp, null));
        _ = factory.Services.GetRequiredService<ConnectionDraftStore>().Delete(draft.Connection.Id, draft.Version, "admin", null);
        Assert.Null(store.Read(draft.Connection.Id));
    }

    [Fact]
    public async Task SavedTrustReachesTheHostHttpConnectionTestAndInputPreview()
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "localhost");
        await using var peer = new ConnectionTrustRotationTests.Peer(true, certificate);
        using var directory = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(directory.Path,
            new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = peer.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var recipient = $"https://localhost:{peer.Port}/";
        var draft = drafts.Write(null, new(drafts.Read().Version, "tls", "TLS", ConnectionKind.Http, Http: new(recipient)), "fixture", null);
        var request = new { connectionId = draft.Connection.Id, connection = new { baseAddress = recipient }, authentication = "none" };
        using var first = await client.PostJsonAsync("/api/edge-admin/v1/connections/test/http", request, Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var initial = await first.Content.ReadFromJsonAsync<HttpConnectionTestResult>(Token);
        Assert.Equal("certificate.untrusted", initial!.Code);
        var observed = initial.Certificate!;
        using var saved = await Put(client, draft.Connection.Id, new(draft.Version, recipient, TlsTrustMode.PinnedCertificate,
            observed.CertificateDerBase64, observed.Sha256Fingerprint, true));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var second = await client.PostJsonAsync("/api/edge-admin/v1/connections/test/http", request, Token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<HttpConnectionTestResult>(Token))!.Success);
        Assert.Equal(1, peer.Messages);
        var sample = await factory.Services.GetRequiredService<HttpReadPreview>().RunAsync(new HttpInputSettings
        { ConnectionId = draft.Connection.Id, BaseAddress = recipient, Fields = JsonSerializer.SerializeToElement(Array.Empty<object>()) },
            ClockSyncState.Synchronized, Token);
        Assert.True(sample.Success);
        Assert.Equal(2, peer.Messages);
    }

    private static ConnectionDraftResult Draft(IServiceProvider services)
    {
        var drafts = services.GetRequiredService<ConnectionDraftStore>();
        return drafts.Write(null, new(drafts.Read().Version, "tls", "TLS", ConnectionKind.Http, Http: new(Recipient)), "fixture", null);
    }

    private static ConnectionTrustRequest Request(string draftVersion)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "localhost");
        var info = ServerCertificateTrust.Describe(certificate)!;
        return new(draftVersion, Recipient, TlsTrustMode.PinnedCertificate, info.CertificateDerBase64, info.Sha256Fingerprint, true);
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, long id, ConnectionTrustRequest request) =>
        client.PutAsJsonAsync(Path(id), request, Json, Token);
}

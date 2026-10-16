using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Adapters;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class ClientCertificateReviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Theory]
    [InlineData(false, false, "Configurator")]
    [InlineData(false, true, "Administrator")]
    [InlineData(true, false, "Administrator")]
    [InlineData(true, true, "Configurator")]
    public async Task ReviewIsPublicOnlyReadOnlyAndSavingStillChecksCurrentVersion(bool mqtt, bool pfx, string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await HttpClientCertificateCredentialTests.AdminAsync(factory);
        var request = CreateDraft(factory, mqtt, Issue(pfx: pfx), out var id);
        TestAccounts.Create(factory.Services, "editor", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var editor = await factory.CreateLoggedInClientAsync("editor", Token);
        using var response = await ReviewAsync(editor, id, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = await response.Content.ReadFromJsonAsync<ClientCertificateReview>(Json, Token);
        Assert.NotNull(result);
        Assert.Null(result.Current);
        Assert.True(result.IdentityChanged);
        Assert.False(result.AccountDeclarationRequired);
        Assert.Equal("CN=review-client", result.Certificate.Subject);
        Assert.Matches("^[0-9A-F]{64}$", result.Certificate.Sha256Fingerprint);
        Assert.Equal(request.ExpectedDraftVersion, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Null(factory.Services.GetRequiredService<HttpCredentialStore>().Read(id));
        Assert.Null(factory.Services.GetRequiredService<MqttCredentialStore>().Read(id));
        Assert.Empty(TestAccounts.Audit(factory.Services, "credential.http.write"));
        Assert.Empty(TestAccounts.Audit(factory.Services, "credential.mqtt.write"));
        AssertSafe(await response.Content.ReadAsStringAsync(Token), request.Certificate);
        var version = await SaveAsync(factory, id, request);
        using var stale = await ReviewAsync(editor, id, request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("credential.versionConflict", await stale.ReadProblemCodeAsync(Token));
        // The original writer is authoritative even after a successful review.
        var conflict = await Assert.ThrowsAsync<CredentialWriteException>(() => SaveAsync(factory, id, request));
        Assert.Equal("credential.versionConflict", conflict.Code);
        using var refreshed = await ReviewAsync(editor, id, request with { ExpectedVersion = version });
        var next = await refreshed.Content.ReadFromJsonAsync<ClientCertificateReview>(Json, Token);
        Assert.False(next!.IdentityChanged);
        Assert.Equal("clientCertificate", next.Current!.Kind);
        Assert.Equal("CN=review-client", next.Current.Account);
        AssertSafe(await refreshed.Content.ReadAsStringAsync(Token), request.Certificate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LiveQueueCountsAndSameSubjectDeclarationAreNotAFrozenPermission(bool mqtt, bool failed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await HttpClientCertificateCredentialTests.AdminAsync(factory);
        var request = CreateDraft(factory, mqtt, Issue(), out var id);
        request = request with { ExpectedVersion = await SaveAsync(factory, id, request) };
        using var before = await ReviewAsync(client, id, request);
        Assert.False((await before.Content.ReadFromJsonAsync<ClientCertificateReview>(Json, Token))!.AccountDeclarationRequired);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), id, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), "fixture", "fixture", "event", null)], Token)).Committed);
        if (failed)
        { Assert.True((await cache.RecordFailureAsync(new(Assert.Single(cache.ReadPending(id, 0, 1)).Seq, 0, "rejected", true, false), Token)).Committed); }
        var conflict = await Assert.ThrowsAsync<CredentialWriteException>(() => SaveAsync(factory, id, request));
        Assert.Equal("credential.accountDeclarationRequired", conflict.Code);
        using var response = await ReviewAsync(client, id, request);
        var result = await response.Content.ReadFromJsonAsync<ClientCertificateReview>(Json, Token);
        Assert.True(result!.AccountDeclarationRequired);
        Assert.False(result.IdentityChanged);
        Assert.Equal(failed ? 0 : 1, result.PendingCount);
        Assert.Equal(failed ? 1 : 0, result.FailedCount);
        using var changed = await ReviewAsync(client, id, request with { Certificate = Issue("another-account") });
        var changedResult = await changed.Content.ReadFromJsonAsync<ClientCertificateReview>(Json, Token);
        Assert.True(changedResult!.IdentityChanged);
        Assert.False(changedResult.AccountDeclarationRequired);
        Assert.Equal(request.ExpectedVersion, mqtt ? factory.Services.GetRequiredService<MqttCredentialStore>().Read(id)!.Version : factory.Services.GetRequiredService<HttpCredentialStore>().Read(id)!.Version);
    }

    [Theory]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("csrf", HttpStatusCode.Forbidden)]
    [InlineData("plain", HttpStatusCode.BadRequest)]
    [InlineData("private", HttpStatusCode.BadRequest)]
    // TestServer has no Kestrel body limiter: parser bounds still reject. Browser suite verifies real 413.
    [InlineData("oversize", HttpStatusCode.BadRequest)]
    [InlineData("draft", HttpStatusCode.Conflict)]
    [InlineData("recipient", HttpStatusCode.Conflict)]
    [InlineData("protocol", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.Conflict)]
    public async Task InvalidOrUnauthorizedReviewCannotReadIdentityOrWrite(string scenario, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await HttpClientCertificateCredentialTests.AdminAsync(factory);
        var request = CreateDraft(factory, false, Issue(), out var id);
        using var anonymous = factory.CreateClient();
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var viewer = await factory.CreateLoggedInClientAsync("viewer", Token);
        var client = scenario == "anonymous" ? anonymous : scenario == "viewer" ? viewer : admin;
        if (scenario == "csrf")
        { client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
        request = scenario switch
        {
            "plain" => request with { Recipient = "http://localhost:8883/" },
            "private" => request with { Certificate = new(CertificatePem: "fixture-secret", PrivateKeyPem: "fixture-secret") },
            "oversize" => request with { Certificate = new(Pkcs12: new string('A', 300_000)) },
            "draft" => request with { ExpectedDraftVersion = "stale" },
            "recipient" => request with { Recipient = "https://localhost:8884/" },
            "protocol" => request with { Kind = ConnectionKind.Mqtt, Recipient = "mqtts://localhost:8883" },
            _ => request,
        };
        using var response = await ReviewAsync(client, scenario == "missing" ? id + 1 : id, request);
        Assert.Equal(expected, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        AssertSafe(text, request.Certificate);
        Assert.DoesNotContain("CN=review-client", text, StringComparison.Ordinal);
        Assert.Null(factory.Services.GetRequiredService<HttpCredentialStore>().Read(id));
        Assert.Empty(TestAccounts.Audit(factory.Services, "credential.http.write"));
    }

    private static ClientCertificateReviewRequest CreateDraft(TenonEdgeApplicationFactory factory, bool mqtt, ClientCertificateUpload upload, out long id)
    {
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var result = drafts.Write(null, mqtt
            ? new(drafts.Read().Version, "mqtt", "MQTT", ConnectionKind.Mqtt, Mqtt: new("localhost", 8883, Tls: true))
            : new ConnectionDraftRequest(drafts.Read().Version, "http", "HTTP", ConnectionKind.Http, Http: new("https://localhost:8883/")), "admin", null);
        id = result.Connection.Id;
        return new(mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http, mqtt ? "mqtts://localhost:8883" : "https://localhost:8883/", result.Version, null, upload);
    }

    private static async Task<string> SaveAsync(TenonEdgeApplicationFactory factory, long id, ClientCertificateReviewRequest request)
    {
        if (request.Kind == ConnectionKind.Mqtt)
        {
            return (await factory.Services.GetRequiredService<MqttCredentialStore>().WriteAsync(id, new(request.Recipient, ExpectedVersion: request.ExpectedVersion,
            ExpectedDraftVersion: request.ExpectedDraftVersion, Kind: MqttCredentialKind.ClientCertificate, Certificate: request.Certificate), "admin", null, true, Token)).Version;
        }
        return (await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(id, new(request.Recipient, HttpCredentialKind.ClientCertificate,
            ExpectedVersion: request.ExpectedVersion, ExpectedDraftVersion: request.ExpectedDraftVersion, Certificate: request.Certificate), "admin", null, true, Token)).Version;
    }
    private static Task<HttpResponseMessage> ReviewAsync(HttpClient client, long id, ClientCertificateReviewRequest request) =>
        client.PostAsJsonAsync(new Uri($"/api/edge-admin/v1/connections/{id}/client-certificate-review", UriKind.Relative), request, Json, Token);

    private static ClientCertificateUpload Issue(string name = "review-client", bool pfx = false)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, name, clientOnly: true);
        return pfx ? new(Pkcs12: Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "fixture-password")), Password: "fixture-password")
            : new(CertificatePem: certificate.ExportCertificatePem(), PrivateKeyPem: key.ExportPkcs8PrivateKeyPem());
    }
    private static void AssertSafe(string text, ClientCertificateUpload upload)
    {
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", text, StringComparison.Ordinal);
        if (upload.Pkcs12 is { Length: > 0 })
        { Assert.DoesNotContain(upload.Pkcs12, text, StringComparison.Ordinal); }
    }
}

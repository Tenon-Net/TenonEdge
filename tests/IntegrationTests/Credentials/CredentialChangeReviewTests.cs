using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class CredentialChangeReviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private const string Secret = "fixture-secret-only-sent-to-writer";

    [Theory]
    [InlineData("password")]
    [InlineData("basic")]
    [InlineData("bearer")]
    [InlineData("apiKeyHeader")]
    [InlineData("apiKeyQuery")]
    public async Task PublicReviewIsReadOnlyAndFinalWriterRequiresDeclarationForNewAccumulation(string kind)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await HttpClientCertificateCredentialTests.AdminAsync(factory);
        TestAccounts.Create(factory.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        using var editor = await factory.CreateLoggedInClientAsync("editor", Token);
        var (id, request) = CreateDraft(factory, kind);
        var initial = await ReviewAsync(editor, id, request);
        Assert.Null(initial.Current);
        Assert.True(initial.IdentityChanged);
        Assert.False(initial.AccountDeclarationRequired);
        Assert.Null(factory.Services.GetRequiredService<HttpCredentialStore>().Read(id));
        Assert.Null(factory.Services.GetRequiredService<MqttCredentialStore>().Read(id));
        request = request with { ExpectedVersion = await SaveAsync(factory, id, request) };
        var before = await ReviewAsync(editor, id, request);
        Assert.False(before.IdentityChanged);
        Assert.False(before.AccountDeclarationRequired);
        Assert.Equal(kind, before.Current!.Kind);
        Assert.Equal(request.Username, before.Current.Account);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([Draft(id), Draft(id)], Token)).Committed);
        Assert.True((await cache.RecordFailureAsync(new(cache.ReadPending(id, 0, 2)[0].Seq, 0, "rejected", true, false), Token)).Committed);
        var conflict = await Assert.ThrowsAsync<CredentialWriteException>(() => SaveAsync(factory, id, request));
        Assert.Equal("credential.accountDeclarationRequired", conflict.Code);
        var result = await ReviewAsync(editor, id, request);
        Assert.True(result.AccountDeclarationRequired);
        Assert.Equal(1, result.PendingCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(request.ExpectedDraftVersion, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        // Public name changes do not establish a new account for opaque API keys.
        if (request.Name is not null)
        {
            var renamed = await ReviewAsync(editor, id, request with { Name = "X-Another-Key" });
            Assert.False(renamed.IdentityChanged);
            Assert.True(renamed.AccountDeclarationRequired);
        }
        else if (request.Username is not null)
        {
            var changed = await ReviewAsync(editor, id, request with { Username = "another-account" });
            Assert.True(changed.IdentityChanged);
            Assert.False(changed.AccountDeclarationRequired);
        }
        var auditKind = kind == "password" ? "mqtt" : "http";
        Assert.Single(TestAccounts.Audit(factory.Services, $"credential.{auditKind}.write"));
        var saved = await SaveAsync(factory, id, request, false);
        Assert.NotEqual(request.ExpectedVersion, saved);
        using var stale = await PostAsync(editor, id, request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("credential.versionConflict", await stale.ReadProblemCodeAsync(Token));
    }

    [Theory]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("csrf", HttpStatusCode.Forbidden)]
    [InlineData("draft", HttpStatusCode.Conflict)]
    [InlineData("recipient", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.Conflict)]
    [InlineData("protocol", HttpStatusCode.Conflict)]
    [InlineData("kind", HttpStatusCode.BadRequest)]
    [InlineData("username", HttpStatusCode.BadRequest)]
    [InlineData("name", HttpStatusCode.BadRequest)]
    [InlineData("certificate", HttpStatusCode.BadRequest)]
    [InlineData("secret", HttpStatusCode.BadRequest)]
    public async Task RejectsInvalidUnauthorizedAndSecretBearingRequests(string scenario, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await HttpClientCertificateCredentialTests.AdminAsync(factory);
        var (id, request) = CreateDraft(factory, "basic");
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var viewer = await factory.CreateLoggedInClientAsync("viewer", Token);
        using var anonymous = factory.CreateClient();
        var client = scenario == "viewer" ? viewer : scenario == "anonymous" ? anonymous : admin;
        if (scenario == "csrf")
        { client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
        request = scenario switch
        {
            "draft" => request with { ExpectedDraftVersion = "stale" },
            "recipient" => request with { Recipient = "https://another.test/" },
            "protocol" => request with { Kind = ConnectionKind.Mqtt, Recipient = "mqtt://localhost:1883", AuthenticationKind = "password" },
            "kind" => request with { Kind = ConnectionKind.Modbus },
            "username" => request with { Username = "bad:name" },
            "name" => request with { AuthenticationKind = "apiKeyHeader", Username = null, Name = "Host" },
            "certificate" => request with { AuthenticationKind = "clientCertificate" },
            _ => request,
        };
        var body = JsonSerializer.SerializeToNode(request, Json)!;
        if (scenario == "secret")
        { body["secret"] = Secret; }
        using var response = await client.PostAsJsonAsync(new Uri($"/api/edge-admin/v1/connections/{(scenario == "missing" ? id + 1 : id)}/credential-review", UriKind.Relative), body, Json, Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Empty(TestAccounts.Audit(factory.Services, "credential.http.write"));
        Assert.Null(factory.Services.GetRequiredService<HttpCredentialStore>().Read(id));
    }

    private static (long Id, CredentialChangeReviewRequest Request) CreateDraft(TenonEdgeApplicationFactory factory, string kind)
    {
        var mqtt = kind == "password";
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var result = drafts.Write(null, mqtt
            ? new(drafts.Read().Version, "mqtt", "MQTT", ConnectionKind.Mqtt, Mqtt: new("localhost", 1883))
            : new ConnectionDraftRequest(drafts.Read().Version, "http", "HTTP", ConnectionKind.Http, Http: new("https://localhost:8883/")), "admin", null);
        return (result.Connection.Id, new(mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
            mqtt ? "mqtt://localhost:1883" : "https://localhost:8883/", result.Version, null, kind,
            kind is "basic" or "password" ? "original-account" : null, kind.StartsWith("apiKey", StringComparison.Ordinal) ? "X-Key" : null));
    }

    private static async Task<string> SaveAsync(TenonEdgeApplicationFactory factory, long id, CredentialChangeReviewRequest request, bool? changed = null)
    {
        if (request.Kind == ConnectionKind.Mqtt)
        {
            return (await factory.Services.GetRequiredService<MqttCredentialStore>().WriteAsync(id, new(request.Recipient, request.Username, Secret,
                request.ExpectedVersion, changed, ExpectedDraftVersion: request.ExpectedDraftVersion), "admin", null, true, Token)).Version;
        }
        var kind = Enum.Parse<HttpCredentialKind>(request.AuthenticationKind, ignoreCase: true);
        return (await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(id, new(request.Recipient, kind, request.Username, request.Name,
            Secret, request.ExpectedVersion, changed, ExpectedDraftVersion: request.ExpectedDraftVersion), "admin", null, true, Token)).Version;
    }

    private static DeliveryDraft Draft(long id) => new(Guid.NewGuid(), id, Guid.NewGuid(), "meter", 1, "{}"u8.ToArray(), "fixture", "fixture", "event", null);
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, long id, CredentialChangeReviewRequest request) =>
        client.PostAsJsonAsync(new Uri($"/api/edge-admin/v1/connections/{id}/credential-review", UriKind.Relative), request, Json, Token);
    private static async Task<CredentialChangeReview> ReviewAsync(HttpClient client, long id, CredentialChangeReviewRequest request)
    {
        using var response = await PostAsync(client, id, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        return (await response.Content.ReadFromJsonAsync<CredentialChangeReview>(Json, Token))!;
    }
}

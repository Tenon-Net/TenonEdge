using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class MqttConnectionTestApiTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private const string Endpoint = "/api/edge-admin/v1/connections/test/mqtt";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MqttConnectionTestRequest Request => new(new("127.0.0.1"));

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RoleGuardAndRealOutboundPolicyApplyWithoutChangingDraft(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var before = store.Read();
        using var response = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token);
            Assert.Equal("outbound.denied", result!.Code);
        }
        var after = store.Read();
        Assert.Equal(before.Version, after.Version);
        Assert.Empty(after.Connections);
    }

    [Fact]
    public async Task AnonymousCsrfAndMalformedAuthenticationAreRejected()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var invalid = Request with { Password = "fixture-secret" };
        Assert.DoesNotContain("fixture-secret", invalid.ToString(), StringComparison.Ordinal);
        using var malformed = await client.PostJsonAsync(Endpoint, invalid, Token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain("fixture-secret", await malformed.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        using var noCsrf = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
    }

    [Fact]
    public async Task MissingStoredCredentialsNeverFallBackToAnonymous()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint, Request with
        { Authentication = ConnectionTestAuthentication.Stored, CredentialConnectionId = 123 }, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("credential.unavailable", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLimitUsesAuthenticatedTicketNotAccountOrClientInput()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var first = await factory.CreateLoggedInClientAsync("tester", Token);
        using var second = await factory.CreateLoggedInClientAsync("tester", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var one = budget.TryAcquire(key);
        using var two = budget.TryAcquire(key);
        using var rejected = await first.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        using var independent = await second.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
    }

    [Fact]
    [Trait("Category", "RealBroker")]
    public async Task ProvidedAndBoundStoredCredentialsReachRealBrokerWithoutDraftMutation()
    {
        var broker = fixture.Require();
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        factory.Services.GetRequiredService<OutboundPolicyStore>().Write(new([], [broker.Port]), "fixture", null);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var saved = drafts.Write(null, new(drafts.Read().Version, "mqtt-test", "MQTT", ConnectionKind.Mqtt,
            Mqtt: new("127.0.0.1", broker.Port)), "fixture", null);
        var credentials = factory.Services.GetRequiredService<MqttCredentialStore>();
        await credentials.WriteAsync(saved.Connection.Id, new($"mqtt://127.0.0.1:{broker.Port}", MosquittoBroker.UserName,
            MosquittoBroker.Password, ExpectedDraftVersion: saved.Version), "fixture", null, false, token: TestContext.Current.CancellationToken);
        var metadata = credentials.Read(saved.Connection.Id);
        var request = new MqttConnectionTestRequest(new("127.0.0.1", broker.Port), ConnectionTestAuthentication.Provided,
            Username: MosquittoBroker.UserName, Password: MosquittoBroker.Password);
        using var provided = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, provided.StatusCode);
        Assert.True((await provided.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token))!.Success);
        request = request with
        {
            Authentication = ConnectionTestAuthentication.Stored,
            CredentialConnectionId = saved.Connection.Id,
            Username = null,
            Password = null
        };
        using var stored = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.True((await stored.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token))!.Success);
        using var redirected = await client.PostJsonAsync(Endpoint, request with { Connection = request.Connection with { Host = "localhost" } }, Token);
        Assert.Equal(HttpStatusCode.Conflict, redirected.StatusCode);
        Assert.Equal(saved.Version, drafts.Read().Version);
        Assert.Equal(metadata, credentials.Read(saved.Connection.Id));
    }

    [Fact]
    public async Task GlobalBudgetRejectsImmediatelyAndRecoversAfterRelease()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var a1 = budget.TryAcquire("a");
        using var a2 = budget.TryAcquire("a");
        using var b1 = budget.TryAcquire("b");
        using var b2 = budget.TryAcquire("b");
        using var busy = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        b2!.Dispose();
        using var retry = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }
}

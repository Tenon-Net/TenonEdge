using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class HttpConnectionTestApiTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections/test/http";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static HttpConnectionTestRequest Request => new(new("http://127.0.0.1:18888/"));

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task GuardedUnsavedTestUsesRealPolicyAndDoesNotSaveDraft(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var before = drafts.Read().Version;
        using var response = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<HttpConnectionTestResult>(Token);
            Assert.Equal("outbound.denied", result!.Code);
            Assert.Equal("failed", result.Steps[1].Status);
        }
        Assert.Equal(before, drafts.Read().Version);
        Assert.Empty(drafts.Read().Connections);
    }

    [Fact]
    public async Task AnonymousCsrfAndSecretHeaderInjectionAreRejectedBeforeNetwork()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var invalid = await client.PostJsonAsync(Endpoint, Request with
        { Connection = Request.Connection with { Headers = new() { ["X-Secret"] = "fixture-not-logged" } } }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain("fixture-not-logged", await invalid.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task StoredCredentialRequiresExistingHttpDraftAndExactRecipient()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var saved = drafts.Write(null, new(drafts.Read().Version, "http", "HTTP", ConnectionKind.Http, Http: Request.Connection), "fixture", null);
        var request = Request with { ConnectionId = saved.Connection.Id, Authentication = ConnectionTestAuthentication.Stored };
        using var absent = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Conflict, absent.StatusCode);
        await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(saved.Connection.Id,
            new(Request.Connection.BaseAddress, HttpCredentialKind.Bearer, Secret: "fixture-not-logged", ExpectedDraftVersion: saved.Version), "fixture", null, false, token: TestContext.Current.CancellationToken);
        using var bound = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
        Assert.Equal("outbound.denied", (await bound.Content.ReadFromJsonAsync<HttpConnectionTestResult>(Token))!.Code);
        using var changed = await client.PostJsonAsync(Endpoint, request with { Connection = new("http://127.0.0.1:18888/other/") }, Token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(saved.Version, drafts.Read().Version);
        var transient = new HttpTestCredential(HttpCredentialKind.Bearer, "fixture-not-logged");
        Assert.DoesNotContain("fixture-not-logged", transient.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-not-logged", (Request with { Credential = transient }).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpAndMqttShareTheSameSessionAdmissionBudget()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire(key);
        using var second = budget.TryAcquire(key);
        using var http = await client.PostJsonAsync(Endpoint, Request, Token);
        using var mqtt = await client.PostJsonAsync("/api/edge-admin/v1/connections/test/mqtt", new MqttConnectionTestRequest(new("127.0.0.1")), Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, http.StatusCode);
        Assert.Equal(http.StatusCode, mqtt.StatusCode);
    }
}

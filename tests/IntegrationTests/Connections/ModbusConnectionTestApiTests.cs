using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class ModbusConnectionTestApiTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections/test/modbus";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ModbusConnectionTestRequest Request => new(new("127.0.0.1", 18888));

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
            var result = await response.Content.ReadFromJsonAsync<ModbusConnectionTestResult>(Token);
            Assert.Equal("outbound.denied", result!.Code);
            Assert.Equal("failed", result.Steps[1].Status);
        }
        Assert.Equal(before, drafts.Read().Version);
        Assert.Empty(drafts.Read().Connections);
    }

    [Fact]
    public async Task AnonymousCsrfAndInvalidSettingsAreRejectedBeforeNetwork()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var invalid = await client.PostJsonAsync(Endpoint, Request with
        { Connection = Request.Connection with { Host = "http://fixture-not-logged/" } }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain("fixture-not-logged", await invalid.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint, Request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task ExistingIdRequiresModbusDraftAndMatchingRuntimeSettings()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var missing = await client.PostJsonAsync(Endpoint, Request with { ConnectionId = 123 }, Token);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var saved = drafts.Write(null, new(drafts.Read().Version, "modbus", "Modbus", ConnectionKind.Modbus, Modbus: Request.Connection), "fixture", null);
        var settings = new ModbusConnectionSettings { ConnectionId = saved.Connection.Id, Host = "127.0.0.1", Port = 18888 };
        await using var lease = factory.Services.GetRequiredService<ISharedConnections>().Acquire(settings.ConnectionId, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], factory.Services.GetRequiredService<IOutboundConnector>(), TimeProvider.System));
        using var conflict = await client.PostJsonAsync(Endpoint, Request with
        { ConnectionId = saved.Connection.Id, Connection = Request.Connection with { Port = 18889 } }, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(saved.Version, drafts.Read().Version);
    }

    [Fact]
    public async Task ModbusAndMqttShareTheSameSessionAdmissionBudget()
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

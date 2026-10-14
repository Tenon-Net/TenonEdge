using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.SystemInfo;

public sealed class GatewayNameTests
{
    private const string Endpoint = "/api/edge-admin/v1/system/name";
    private const string Info = "/api/edge-admin/v1/system/info";
    private const string Action = "system.gatewayNameChanged";
    private static readonly string[] ConcurrentNames = ["name-a", "name-b"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task OnlyAdministratorCanRenameButEveryRoleCanRead(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest("一号网关", null), Token);
        Assert.Equal(expected, response.StatusCode);
        using var info = await client.GetAsync(Info, Token);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.True(info.Headers.CacheControl!.NoStore);
        Assert.Equal(expected == HttpStatusCode.OK ? "一号网关" : null, (await info.ReadJsonAsync(Token)).GetProperty("gatewayName").GetString());
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, Action).Count);
    }

    [Fact]
    public async Task AnonymousAndMissingCsrfAreRejected()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest("name", null), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest("name", null), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" \t\n", null)]
    [InlineData("  <现场网关>  ", "<现场网关>")]
    [InlineData("生产😀", "生产😀")]
    public async Task NormalizationAndNoOpDoNotInventAnAuditChange(string? input, string? expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var written = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(input, null), Token);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.True(written.Headers.CacheControl!.NoStore);
        var result = await written.ReadJsonAsync(Token);
        Assert.Equal(expected, result.GetProperty("name").GetString());
        Assert.Equal(expected is not null, result.GetProperty("changed").GetBoolean());
        using var repeated = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(input, expected), Token);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.False((await repeated.ReadJsonAsync(Token)).GetProperty("changed").GetBoolean());
        Assert.Equal(expected is null ? 0 : 1, TestAccounts.Audit(factory.Services, Action).Count);
    }

    [Fact]
    public async Task SixtyFourCharactersAcceptedButLongNamesControlsAndUnknownOrMissingFieldsAreRejected()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var limit = new string('网', 64);
        using var accepted = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(limit, null), Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        foreach (var name in new[] { new string('x', 65), "a\nb", "a\0b", "a\u007fb" })
        {
            using var invalid = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(name, limit), Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var body in new[] { "{}", "{\"name\":null}", "{\"expectedName\":null}", "{\"name\":null,\"expectedName\":null,\"nodeId\":\"x\"}" })
        {
            using var request = new StringContent(body, Encoding.UTF8, "application/json");
            using var invalid = await admin.PutAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(limit, (await admin.GetJsonAsync(Info, Token)).GetProperty("gatewayName").GetString());
        Assert.Single(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task ConcurrentOriginalNamePreconditionsPermitOnlyOneWriteAndAudit()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var responses = await Task.WhenAll(ConcurrentNames.Select(name =>
            admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(name, null), Token)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var audit = Assert.Single(TestAccounts.Audit(factory.Services, Action));
            var name = (await admin.GetJsonAsync(Info, Token)).GetProperty("gatewayName").GetString();
            using var detail = JsonDocument.Parse(audit.Detail!);
            Assert.Equal(name, detail.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("previousName").ValueKind);
            Assert.Equal("admin", audit.AccountName);
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
    }

    [Fact]
    public async Task NameAndAuditSurviveRestartAndClearingDoesNotChangeIdentitySetupOrDraft()
    {
        using var data = TemporaryDirectory.Create();
        string nodeId;
        string firstSetup;
        string draft;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var admin = factory.CreateClient();
            using var setup = await admin.CompleteSetupAsync(Token, gatewayName: "original");
            await admin.UseSessionAsync(setup, Token);
            var info = await admin.GetJsonAsync(Info, Token);
            nodeId = info.GetProperty("nodeId").GetString()!;
            firstSetup = info.GetProperty("firstSetup").GetRawText();
            draft = (await admin.GetJsonAsync("/api/edge-admin/v1/devices", Token)).GetRawText();
            using var changed = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest("new-name", "original"), Token);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await restarted.CreateLoggedInClientAsync("admin", Token);
        var after = await client.GetJsonAsync(Info, Token);
        Assert.Equal("new-name", after.GetProperty("gatewayName").GetString());
        using var stale = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(null, "original"), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var clear = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest(null, "new-name"), Token);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var current = await client.GetJsonAsync(Info, Token);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("gatewayName").ValueKind);
        Assert.Equal(nodeId, current.GetProperty("nodeId").GetString());
        Assert.Equal(firstSetup, current.GetProperty("firstSetup").GetRawText());
        Assert.Equal(draft, (await client.GetJsonAsync("/api/edge-admin/v1/devices", Token)).GetRawText());
        var state = restarted.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PendingRevision);
        Assert.Null(state.RunningRevision);
        Assert.Equal(2, TestAccounts.Audit(restarted.Services, Action).Count);
    }

    [Fact]
    public async Task AuditFailureRollsBackNameInsteadOfReportingUnauditedSuccess()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_name_audit BEFORE INSERT ON audit_log WHEN NEW.action = '{Action}' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        using var response = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new GatewayNameRequest("rejected", null), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync(Info, Token)).GetProperty("gatewayName").ValueKind);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }
}

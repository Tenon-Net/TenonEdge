using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Users;

public sealed partial class UserManagementTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("configurator", HttpStatusCode.Forbidden)]
    [InlineData("temporary", HttpStatusCode.Forbidden)]
    public async Task EveryUserEndpointEnforcesAdministratorAndCompletedPasswordChange(string account, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        if (account != "anonymous")
        {
            TestAccounts.Create(factory.Services, account, ApiRequests.StrongPassword,
                account == "temporary" ? UserRole.Administrator : Enum.Parse<UserRole>(account, true), mustChangePassword: account == "temporary");
        }
        using var client = account == "anonymous" ? factory.CreateClient() : await factory.CreateLoggedInClientAsync(account, Token);
        var operations = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, Root), (HttpMethod.Post, Root), (HttpMethod.Put, Root + "/1"),
            (HttpMethod.Post, Root + "/1/password"), (HttpMethod.Post, Root + "/1/unlock"),
            (HttpMethod.Post, Root + "/1/end-sessions"), (HttpMethod.Delete, Root + "/1?confirmed=true"),
        };
        foreach (var (method, path) in operations)
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) };
            using var response = await client.SendAsync(request, Token);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("password")]
    [InlineData("unlock")]
    [InlineData("end-sessions")]
    [InlineData("delete")]
    public async Task EveryWriteRequiresCsrfAndExistingUserMutationsRequireConfirmation(string action)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var user = await UserAsync(admin, "target");
        var csrf = admin.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using (var denied = await Send())
        { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        Assert.Equal(user.GetRawText(), (await UserAsync(admin, "target")).GetRawText());
        if (action != "create")
        {
            admin.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
            using var unconfirmed = action == "update" ? await UpdateAsync(admin, user, confirmed: false) : await CommandAsync(admin, user, action, confirmed: false);
            await AssertProblemAsync(unconfirmed, HttpStatusCode.BadRequest, "user.invalidConfirmation");
        }
        Task<HttpResponseMessage> Send() => action switch
        {
            "create" => admin.PostJsonAsync(Root, new { accountName = "new-user", role = "viewer", temporaryPassword = ApiRequests.StrongPassword }, Token),
            "update" => UpdateAsync(admin, user, displayName: "changed"),
            _ => CommandAsync(admin, user, action),
        };
    }

    [Theory]
    [InlineData("role")]
    [InlineData("disable")]
    [InlineData("password")]
    [InlineData("end-sessions")]
    [InlineData("delete")]
    public async Task SecurityChangesInvalidateAllExistingSessionsOnTheirNextRequest(string action)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var first = await factory.CreateLoggedInClientAsync("target", Token);
        using var second = await factory.CreateLoggedInClientAsync("target", Token);
        await first.GetJsonAsync(ApiRequests.Session, Token);
        await second.GetJsonAsync(ApiRequests.Session, Token);
        var user = await UserAsync(admin, "target");
        using var changed = action switch
        {
            "role" => await UpdateAsync(admin, user, role: "configurator"),
            "disable" => await UpdateAsync(admin, user, enabled: false),
            _ => await CommandAsync(admin, user, action),
        };
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        foreach (var session in new[] { first, second })
        {
            using var expired = await session.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }
        var auditAction = action switch { "role" => "changeRole", "password" => "resetPassword", "end-sessions" => "endSessions", _ => action };
        await AssertAuditAsync(admin, "user." + auditAction, "target");
        using var fresh = factory.CreateClient();
        using var login = await fresh.LoginAsync("target", action == "password" ? ApiRequests.AnotherStrongPassword : ApiRequests.StrongPassword, Token);
        Assert.Equal(action is "disable" or "delete" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, login.StatusCode);
        if (action == "password")
        { Assert.True((await login.ReadJsonAsync(Token)).GetProperty("mustChangePassword").GetBoolean()); }
        if (action == "delete")
        {
            Assert.DoesNotContain((await admin.GetJsonAsync(Root, Token)).GetProperty("items").EnumerateArray(), item => item.GetProperty("accountName").GetString() == "target");
        }
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("demote")]
    [InlineData("delete")]
    public async Task LastEnabledAdministratorAndSelfDeletionAreProtected(string action)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "disabled", ApiRequests.StrongPassword, UserRole.Administrator, enabled: false);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var user = await UserAsync(admin, "admin");
        using var response = action == "delete" ? await CommandAsync(admin, user, action) : await UpdateAsync(admin, user,
            role: action == "demote" ? "viewer" : "administrator", enabled: action != "disable");
        await AssertProblemAsync(response, HttpStatusCode.Conflict, action == "delete" ? "user.cannotDeleteSelf" : "user.lastAdministrator");
        Assert.Equal(user.GetRawText(), (await UserAsync(admin, "admin")).GetRawText());
    }

    [Fact]
    public async Task TwoAdministratorsCannotBothDemoteThemselves()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "second", ApiRequests.StrongPassword, UserRole.Administrator);
        using var first = await factory.CreateLoggedInClientAsync("admin", Token);
        using var second = await factory.CreateLoggedInClientAsync("second", Token);
        var one = await UserAsync(first, "admin");
        var two = await UserAsync(second, "second");
        var responses = await Task.WhenAll(UpdateAsync(first, one), UpdateAsync(second, two));
        try
        {
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.OK);
            await AssertProblemAsync(Assert.Single(responses, item => item.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "user.lastAdministrator");
            var remaining = responses[0].StatusCode == HttpStatusCode.OK ? second : first;
            var page = await remaining.GetJsonAsync(Root, Token);
            Assert.Single(page.GetProperty("items").EnumerateArray(), item => item.GetProperty("role").GetString() == "administrator" && item.GetProperty("isEnabled").GetBoolean());
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
    }

    [Fact]
    public async Task DisplayNameOnlyChangePreservesAccountAndSessionsButRejectsOldVersion()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var target = await factory.CreateLoggedInClientAsync("target", Token);
        var original = await UserAsync(admin, "target");
        using var changed = await UpdateAsync(admin, original, displayName: "  新显示名  ");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var session = await target.GetJsonAsync(ApiRequests.Session, Token);
        Assert.Equal("target", session.GetProperty("accountName").GetString());
        Assert.Equal("新显示名", session.GetProperty("displayName").GetString());
        using var stale = await UpdateAsync(admin, original, role: "administrator");
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "user.changed");
        var current = await UserAsync(admin, "target");
        using var noChange = await UpdateAsync(admin, current, displayName: "新显示名");
        await AssertProblemAsync(noChange, HttpStatusCode.Conflict, "user.noChanges");
        await AssertAuditAsync(admin, "user.rename", "target");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    [InlineData("update")]
    public async Task FailedAuditInsertRollsBackUserAndDoesNotRevokeSessions(string action)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var target = await factory.CreateLoggedInClientAsync("target", Token);
        var before = await UserAsync(admin, "target");
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_user_audit BEFORE INSERT ON audit_log WHEN NEW.action LIKE 'user.%' BEGIN SELECT RAISE(ABORT, 'secret-storage-fault'); END;");
        using var response = action switch
        {
            "create" => await admin.PostJsonAsync(Root, new { accountName = "new-user", role = "viewer", temporaryPassword = ApiRequests.AnotherStrongPassword }, Token),
            "update" => await UpdateAsync(admin, before, role: "configurator"),
            _ => await CommandAsync(admin, before, action),
        };
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "user.storageUnavailable");
        Assert.DoesNotContain("secret-storage-fault", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(before.GetRawText(), (await UserAsync(admin, "target")).GetRawText());
        Assert.Equal(2, (await admin.GetJsonAsync(Root, Token)).GetProperty("items").GetArrayLength());
        await target.GetJsonAsync(ApiRequests.Session, Token);
        var audit = await admin.GetJsonAsync("/api/edge-admin/v1/audit?target=target", Token);
        Assert.DoesNotContain(audit.GetProperty("items").EnumerateArray(), item => item.GetProperty("action").GetString()!.StartsWith("user.", StringComparison.Ordinal));
    }
}

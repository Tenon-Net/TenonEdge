using System.Globalization;
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
    private const string Root = "/api/edge-admin/v1/users";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdministratorCreatesTemporaryPasswordUserAndTheUserMustChangeItBeforeReadingManagementData()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var created = await admin.PostJsonAsync(Root, new { accountName = "operator", displayName = "操作人员", role = "configurator", temporaryPassword = ApiRequests.AnotherStrongPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var response = await created.ReadJsonAsync(Token);
        Assert.Equal("operator", response.GetProperty("user").GetProperty("accountName").GetString());
        Assert.True(response.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.DoesNotContain(ApiRequests.AnotherStrongPassword, response.GetRawText(), StringComparison.Ordinal);
        var list = await admin.GetJsonAsync(Root, Token);
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("passwordHash", list.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("securityStamp", list.GetRawText(), StringComparison.Ordinal);
        using var user = factory.CreateClient();
        using var login = await user.LoginAsync("operator", ApiRequests.AnotherStrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await user.UseSessionAsync(login, Token)).GetProperty("mustChangePassword").GetBoolean());
        using var blocked = await user.GetAsync(new Uri("/api/edge-admin/v1/system/info", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("auth.passwordChangeRequired", await blocked.ReadProblemCodeAsync(Token));
        using var changed = await user.PostJsonAsync(ApiRequests.ChangePassword, new { currentPassword = ApiRequests.AnotherStrongPassword, newPassword = "Changed-Temporary-9276" }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False((await user.UseSessionAsync(changed, Token)).GetProperty("mustChangePassword").GetBoolean());
        await user.GetJsonAsync("/api/edge-admin/v1/system/info", Token);
        var audit = Assert.Single(TestAccounts.Audit(factory.Services, "user.create"));
        Assert.Equal("admin", audit.AccountName);
        Assert.Equal("operator", audit.Target);
        Assert.Equal("success", audit.Result);
    }
    private static async Task<JsonElement> UserAsync(HttpClient admin, string name) =>
        (await admin.GetJsonAsync(Root, Token)).GetProperty("items").EnumerateArray().Single(user => user.GetProperty("accountName").GetString() == name);

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient admin, JsonElement user, string role = "viewer", bool enabled = true, string? displayName = null, bool confirmed = true) =>
        admin.PutAsJsonAsync(new Uri($"{Root}/{user.GetProperty("id").GetInt64()}", UriKind.Relative), new
        { expectedVersion = user.GetProperty("version").GetString(), displayName, role, isEnabled = enabled, confirmed }, Token);

    private static Task<HttpResponseMessage> CommandAsync(HttpClient admin, JsonElement user, string action, bool confirmed = true, string? version = null)
    {
        var id = user.GetProperty("id").GetInt64();
        version ??= user.GetProperty("version").GetString();
        return action == "delete"
            ? admin.DeleteAsync(new Uri($"{Root}/{id}?expectedVersion={version}&confirmed={confirmed.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative), Token)
            : admin.PostJsonAsync($"{Root}/{id}/{action}", new { expectedVersion = version, confirmed, temporaryPassword = ApiRequests.AnotherStrongPassword }, Token);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ReadProblemCodeAsync(Token));
    }

    private static async Task AssertAuditAsync(HttpClient admin, string action, string target)
    {
        var page = await admin.GetJsonAsync($"/api/edge-admin/v1/audit?action={action}&target={target}", Token);
        var entry = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("admin", entry.GetProperty("accountName").GetString());
        Assert.Equal("success", entry.GetProperty("result").GetString());
    }
}

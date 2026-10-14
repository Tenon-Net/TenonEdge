using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

public sealed partial class ApiTokenTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreatorDisableOrDeleteRequiresCurrentExplicitChoiceAndKeptTokensRetainTheirOwnRole(bool delete, bool revoke)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        TestAccounts.Create(factory.Services, "owner", ApiRequests.StrongPassword, UserRole.Administrator);
        using var owner = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var login = await owner.LoginAsync("owner", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await owner.UseSessionAsync(login, Token);
        var parent = await Create(owner, "parent", "administrator");
        using var script = Script(factory, parent.Secret);
        var user = (await admin.GetJsonAsync("/api/edge-admin/v1/users", Token)).GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("accountName").GetString() == "owner");
        var id = user.GetProperty("id").GetInt64();
        using var missing = await ChangeCreator(admin, user, delete, null, null);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("token.creatorChoiceRequired", await missing.ReadProblemCodeAsync(Token));
        var review = await admin.GetJsonAsync($"/api/edge-admin/v1/users/{id}/tokens", Token);
        // A token-created child retains source-account provenance without inheriting live user permissions.
        var child = await Create(script, "child", "viewer");
        using var stale = await ChangeCreator(admin, user, delete, review.GetProperty("version").GetString(), revoke);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("token.creatorListChanged", await stale.ReadProblemCodeAsync(Token));
        review = await admin.GetJsonAsync($"/api/edge-admin/v1/users/{id}/tokens", Token);
        Assert.Equal(2, review.GetProperty("items").GetArrayLength());
        var childSummary = review.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("id").GetInt64() == child.Id);
        Assert.Equal("parent", childSummary.GetProperty("createdByTokenName").GetString());
        using var changed = await ChangeCreator(admin, user, delete, review.GetProperty("version").GetString(), revoke);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var stillAdmin = await script.GetAsync("/api/edge-admin/v1/users", Token);
        Assert.Equal(revoke ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, stillAdmin.StatusCode);
        using var childClient = Script(factory, child.Secret);
        using var childRead = await childClient.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(revoke ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, childRead.StatusCode);
        using var cookieEnded = await owner.GetAsync(ApiRequests.Session, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieEnded.StatusCode);
        Assert.Equal(revoke ? 2 : 0, TestAccounts.Audit(factory.Services, "token.revoke").Count);
        if (!revoke)
        {
            using var independentAdministrator = await script.PostJsonAsync("/api/edge-admin/v1/users", new
            { accountName = "created-after", role = "viewer", temporaryPassword = ApiRequests.AnotherStrongPassword }, Token);
            Assert.Equal(HttpStatusCode.OK, independentAdministrator.StatusCode);
            Assert.Equal("parent", Assert.Single(TestAccounts.Audit(factory.Services, "user.create")).TokenName);
        }
    }

    [Fact]
    public async Task UserAndSelectedTokenRevocationsRollBackTogetherIfTheUserAuditCannotCommit()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        TestAccounts.Create(factory.Services, "other-admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var created = await Create(admin, "self-owned", "administrator");
        using var script = Script(factory, created.Secret);
        var user = (await admin.GetJsonAsync("/api/edge-admin/v1/users", Token)).GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("accountName").GetString() == "admin");
        var review = await admin.GetJsonAsync($"/api/edge-admin/v1/users/{user.GetProperty("id").GetInt64()}/tokens", Token);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_disable BEFORE INSERT ON audit_log WHEN NEW.action='user.disable' BEGIN SELECT abs(-9223372036854775808); END;");
        using var failed = await ChangeCreator(admin, user, false, review.GetProperty("version").GetString(), true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, "token.revoke"));
        Assert.Empty(TestAccounts.Audit(factory.Services, "user.disable"));
        using var tokenRead = await script.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.OK, tokenRead.StatusCode);
        await admin.GetJsonAsync(ApiRequests.Session, Token);
        db.Client.Ado.ExecuteCommand("DROP TRIGGER fail_disable");
        using var success = await ChangeCreator(admin, user, false, review.GetProperty("version").GetString(), true);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var ended = await script.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
    }

    private static Task<HttpResponseMessage> ChangeCreator(HttpClient admin, JsonElement user, bool delete, string? expectedTokenVersion, bool? revokeTokens)
    {
        var id = user.GetProperty("id").GetInt64();
        var version = user.GetProperty("version").GetString();
        return delete ? admin.DeleteAsync($"/api/edge-admin/v1/users/{id}?expectedVersion={version}&confirmed=true" +
                (expectedTokenVersion is null ? "" : $"&expectedTokenVersion={expectedTokenVersion}&revokeTokens={revokeTokens!.Value.ToString().ToLowerInvariant()}"), Token)
            : admin.PutAsJsonAsync($"/api/edge-admin/v1/users/{id}", new
            { expectedVersion = version, role = "administrator", isEnabled = false, confirmed = true, expectedTokenVersion, revokeTokens }, Token);
    }
}

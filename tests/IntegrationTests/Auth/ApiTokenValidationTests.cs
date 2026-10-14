using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Features.Users;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

public sealed partial class ApiTokenTests
{
    [Theory]
    [InlineData(null, "viewer")]
    [InlineData("", "viewer")]
    [InlineData("   ", "viewer")]
    [InlineData("embedded\ncontrol", "viewer")]
    [InlineData("valid", null)]
    [InlineData("valid", "owner")]
    public async Task InvalidTokenNamesAndRolesDoNotCreateOrAuditAToken(string? name, string? role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        using var response = await admin.PostJsonAsync(Root, new { name, role }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await admin.GetJsonAsync(Root, Token)).GetProperty("items").EnumerateArray());
        Assert.Empty(TestAccounts.Audit(factory.Services, "token.create"));
    }

    [Fact]
    public async Task NameLengthExpiryAndUnknownFieldsAreValidatedBeforeCreating()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        foreach (var body in new object[] {
            new { name = new string('x', 65), role = "viewer" },
            new { name = "expired", role = "viewer", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new { name = "unknown", role = "viewer", secret = "caller-supplied-secret" } })
        {
            using var response = await admin.PostJsonAsync(Root, body, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(TestAccounts.Audit(factory.Services, "token.create"));
        await Create(admin, new string('x', 64), "viewer");
        Assert.Single(TestAccounts.Audit(factory.Services, "token.create"));
    }

    [Fact]
    public async Task PagingNeverReturnsSecretsAndRevokedNamesCannotBeReusedOrRevokedAgain()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var first = await Create(admin, "first", "viewer");
        var second = await Create(admin, "second", "viewer");
        var page = await admin.GetJsonAsync(Root + "?limit=1", Token);
        Assert.Equal(first.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(first.Id, page.GetProperty("nextCursor").GetInt64());
        var last = await admin.GetJsonAsync($"{Root}?limit=1&after={first.Id}", Token);
        Assert.Equal(second.Id, Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
        Assert.DoesNotContain("secret", page.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.Secret, page.GetRawText(), StringComparison.Ordinal);
        foreach (var query in new[] { "limit=0", "limit=101", "after=-1", "after=9007199254740992" })
        {
            using var invalid = await admin.GetAsync(Root + "?" + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var notConfirmed = await admin.PostJsonAsync($"{Root}/{first.Id}/revoke", new { confirmed = false }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, notConfirmed.StatusCode);
        using var revoked = await admin.PostJsonAsync($"{Root}/{first.Id}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var again = await admin.PostJsonAsync($"{Root}/{first.Id}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var duplicate = await admin.PostJsonAsync(Root, new { name = "first", role = "viewer" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("token.nameExists", await duplicate.ReadProblemCodeAsync(Token));
        Assert.Single(TestAccounts.Audit(factory.Services, "token.revoke"));
        Assert.Equal(2, TestAccounts.Audit(factory.Services, "token.create").Count);
    }

    [Fact]
    public async Task FailedCreationAuditRollsBackTheTokenAndLeavesItsNameAvailable()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_create BEFORE INSERT ON audit_log WHEN NEW.action='token.create' BEGIN SELECT abs(-9223372036854775808); END;");
        using var rejected = await admin.PostJsonAsync(Root, new { name = "atomic", role = "administrator" }, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Empty((await admin.GetJsonAsync(Root, Token)).GetProperty("items").EnumerateArray());
        Assert.Empty(TestAccounts.Audit(factory.Services, "token.create"));
        database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_create");
        await Create(admin, "atomic", "administrator");
        Assert.Single(TestAccounts.Audit(factory.Services, "token.create"));
    }

    [Fact]
    public async Task TheUnrevokedLimitIncludesExpiredTokensAndRevocationReleasesOneSlot()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var creator = database.Client.Queryable<UserAccountRecord>().Single();
        // Seed a full, bounded metadata inventory, not a second implementation of token creation.
        var rows = Enumerable.Range(1, ApiTokenStore.MaximumUnrevoked).Select(i => new ApiTokenRecord
        {
            Id = i,
            Name = "expired-" + i,
            Role = "Viewer",
            SecretHash = new byte[32],
            CreatedAt = 1,
            ExpiresAt = 2,
            CreatorUserId = creator.Id,
            CreatorAccountName = creator.AccountName,
        }).ToArray();
        database.InTransaction(client => client.Insertable(rows).ExecuteCommand());
        using var rejected = await admin.PostJsonAsync(Root, new { name = "overflow", role = "viewer" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("token.limitReached", await rejected.ReadProblemCodeAsync(Token));
        using var revoked = await admin.PostJsonAsync(Root + "/1/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        await Create(admin, "one-slot", "viewer");
        Assert.Single(TestAccounts.Audit(factory.Services, "token.create"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedAdministratorTokenOperationRechecksRevocationInsideTheWriteTransaction(bool create)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var token = await Create(admin, "queued", "administrator");
        var target = TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        var service = factory.Services.GetRequiredService<UserManagementService>();
        var lockout = factory.Services.GetRequiredService<LoginLockout>();
        using var gate = await lockout.EnterAsync(AccountNames.Normalize(create ? "new-user" : "target"), Token);
        var actor = new ManagementActor(null, null, null, token.Id);
        var pending = create ? service.CreateAsync(new("new-user", null, UserRole.Viewer, ApiRequests.StrongPassword), actor, Token)
            : service.ChangeAsync(target.Id, UserOperation.Delete, UserSummary.VersionOf(target), true, actor, null, null, Token);
        Assert.False(pending.IsCompleted);
        using var revoked = await admin.PostJsonAsync($"{Root}/{token.Id}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        gate.Dispose();
        var error = await Assert.ThrowsAsync<UserManagementException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal("user.actorChanged", error.Code);
        var users = (await admin.GetJsonAsync("/api/edge-admin/v1/users", Token)).GetProperty("items");
        Assert.Equal(2, users.GetArrayLength());
        Assert.Contains(users.EnumerateArray(), user => user.GetProperty("accountName").GetString() == "target");
        Assert.Empty(TestAccounts.Audit(factory.Services, create ? "user.create" : "user.delete"));
    }
}

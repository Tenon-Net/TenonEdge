using System.Globalization;
using System.Net;
using System.Text.Json;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Users;

public sealed partial class UserManagementTests
{
    [Theory]
    [InlineData("x", "viewer", "Valid-Test-1234", "user.invalidAccountName")]
    [InlineData("bad name", "viewer", "Valid-Test-1234", "user.invalidAccountName")]
    [InlineData("管理员", "viewer", "Valid-Test-1234", "user.invalidAccountName")]
    [InlineData("new-user", null, "Valid-Test-1234", "user.invalidRole")]
    [InlineData("new-user", "viewer", null, "user.invalidPassword")]
    [InlineData("new-user", "viewer", "short1", "user.invalidPassword")]
    [InlineData("new-user", "viewer", "abcdefgh", "user.invalidPassword")]
    [InlineData("new-user", "viewer", "123456789", "user.invalidPassword")]
    public async Task InvalidCreationIsRejectedWithoutWriting(string name, string? role, string? password, string code)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.PostJsonAsync(Root, new { accountName = name, role, temporaryPassword = password }, Token);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
        Assert.Single((await admin.GetJsonAsync(Root, Token)).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("999")]
    public async Task UnknownRoleIsRejectedByJsonBinding(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.PostJsonAsync(Root, new { accountName = "new-user", role, temporaryPassword = ApiRequests.StrongPassword }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await admin.GetJsonAsync(Root, Token)).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidDisplayNameIsRejectedOnCreationAndUpdate(bool tooLong)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var displayName = tooLong ? new string('x', 129) : "embedded\0control";
        using var create = await admin.PostJsonAsync(Root, new { accountName = "new-user", displayName, role = "viewer", temporaryPassword = ApiRequests.StrongPassword }, Token);
        await AssertProblemAsync(create, HttpStatusCode.BadRequest, "user.invalidDisplayName");
        using var update = await UpdateAsync(admin, await UserAsync(admin, "admin"), role: "administrator", displayName: displayName);
        await AssertProblemAsync(update, HttpStatusCode.BadRequest, "user.invalidDisplayName");
    }

    [Fact]
    public async Task AccountUniquenessIsCaseInsensitiveAndDeletedAccountAuditSurvivesRecreation()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var first = await Create("Operator");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var duplicate = await Create("OPERATOR");
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "user.accountExists");
        var original = await UserAsync(admin, "Operator");
        using var deleted = await CommandAsync(admin, original, "delete");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var recreated = await Create("Operator");
        Assert.Equal(HttpStatusCode.OK, recreated.StatusCode);
        Assert.True((await UserAsync(admin, "Operator")).GetProperty("id").GetInt64() > original.GetProperty("id").GetInt64());
        await AssertAuditAsync(admin, "user.delete", "Operator");
        Task<HttpResponseMessage> Create(string name) => admin.PostJsonAsync(Root, new { accountName = name, role = "viewer", temporaryPassword = ApiRequests.StrongPassword }, Token);
    }

    [Fact]
    public async Task ListUsesBoundedKeysetPagesAndLoginTimeDoesNotChangeEditVersion()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var before = await UserAsync(admin, "target");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastLoginAt").ValueKind);
        using var target = await factory.CreateLoggedInClientAsync("target", Token);
        var after = await UserAsync(admin, "target");
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastLoginAt").ValueKind);
        Assert.Equal(before.GetProperty("version").GetString(), after.GetProperty("version").GetString());
        using var response = await admin.GetAsync(new Uri(Root + "?limit=1", UriKind.Relative), Token);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var first = await response.ReadJsonAsync(Token);
        var cursor = first.GetProperty("nextCursor").GetInt64();
        Assert.Equal("admin", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("accountName").GetString());
        var second = await admin.GetJsonAsync(Root + "?limit=1&after=" + cursor.ToString(CultureInfo.InvariantCulture), Token);
        Assert.Equal("target", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("accountName").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("after=-1")]
    [InlineData("after=9007199254740992")]
    public async Task InvalidPaginationIsRejected(string query)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.GetAsync(new Uri(Root + "?" + query, UriKind.Relative), Token);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "user.invalidQuery");
    }

    [Fact]
    public async Task RealLockoutSurvivesRestartUntilExplicitUnlockAndUnlockDoesNotEnableAccount()
    {
        using var data = TemporaryDirectory.Create();
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
            using var anonymous = factory.CreateClient();
            for (var index = 0; index < 5; index++)
            {
                using var failed = await anonymous.LoginAsync("target", "Wrong-password-127", Token);
                Assert.Equal(index == 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized, failed.StatusCode);
            }
        }
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
            var user = await UserAsync(admin, "target");
            Assert.True(user.GetProperty("lockedUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow);
            using var anonymous = factory.CreateClient();
            using var blocked = await anonymous.LoginAsync("target", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            using var disabled = await UpdateAsync(admin, user, enabled: false);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            user = await UserAsync(admin, "target");
            using var unlocked = await CommandAsync(admin, user, "unlock");
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
            user = await UserAsync(admin, "target");
            Assert.False(user.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, user.GetProperty("lockedUntil").ValueKind);
            await AssertAuditAsync(admin, "user.unlock", "target");
            using var notLocked = await CommandAsync(admin, user, "unlock");
            await AssertProblemAsync(notLocked, HttpStatusCode.Conflict, "user.notLocked");
            using var enabled = await UpdateAsync(admin, user);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        }
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var target = await factory.CreateLoggedInClientAsync("target", Token);
            await target.GetJsonAsync(ApiRequests.Session, Token);
        }
    }
}

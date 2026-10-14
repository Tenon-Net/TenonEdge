using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

public sealed partial class ApiTokenTests
{
    private const string Root = "/api/edge-admin/v1/tokens";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HttpsAdministratorCreatesOneTimeSecretAndRevocationImmediatelyEndsBearerAccess()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var setup = await admin.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        await admin.UseSessionAsync(setup, Token);
        using var created = await admin.PostJsonAsync(Root, new { name = "MES read", role = "viewer" }, Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var result = await created.ReadJsonAsync(Token);
        var secret = result.GetProperty("secret").GetString()!;
        var id = result.GetProperty("token").GetProperty("id").GetInt64();
        Assert.True(created.Headers.CacheControl!.NoStore);
        using var script = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var read = await script.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False(read.Headers.Contains("Set-Cookie"));
        var listing = await admin.GetJsonAsync(Root, Token);
        Assert.DoesNotContain(secret, listing.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("MES read", listing.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.String, listing.GetProperty("items")[0].GetProperty("lastUsedAt").ValueKind);
        using var revoked = await admin.PostJsonAsync($"{Root}/{id}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var rejected = await script.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Theory]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("configurator", HttpStatusCode.OK)]
    [InlineData("administrator", HttpStatusCode.OK)]
    public async Task BearerUsesItsFixedRoleAndActualWritesCarryTheTokenNameWithoutCsrf(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var created = await Create(admin, "MES " + role, role);
        using var script = Script(factory, created.Secret);
        using var write = await script.PostJsonAsync("/api/edge-admin/v1/connections", new
        { expectedVersion = "0", key = "plc", name = "PLC", kind = "modbus", enabled = false, modbus = new { host = "10.0.0.1" } }, Token);
        Assert.Equal(expected, write.StatusCode);
        using var manage = await script.PostJsonAsync(Root, new { name = "child", role = "viewer" }, Token);
        Assert.Equal(role == "administrator" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, manage.StatusCode);
        using var createUser = await script.PostJsonAsync("/api/edge-admin/v1/users", new
        { accountName = "new-user", role = "viewer", temporaryPassword = ApiRequests.AnotherStrongPassword }, Token);
        Assert.Equal(role == "administrator" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, createUser.StatusCode);
        var audits = TestAccounts.Audit(factory.Services, "connection.draftCreated");
        if (expected == HttpStatusCode.OK)
        { Assert.Equal("MES " + role, Assert.Single(audits).TokenName); }
        else
        { Assert.Empty(audits); }
        if (role == "administrator")
        {
            Assert.Equal("MES administrator", Assert.Single(TestAccounts.Audit(factory.Services, "user.create")).TokenName);
            Assert.Equal("MES administrator", TestAccounts.Audit(factory.Services, "token.create").Last().TokenName);
        }
    }

    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task PlainHttpCreationAndCookieWritesWithoutCsrfAreRejected(string address, bool removeCsrf)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory, address);
        if (removeCsrf)
        { admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
        using var response = await admin.PostJsonAsync(Root, new { name = "deny", role = "administrator" }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(removeCsrf ? "auth.csrfTokenInvalid" : "token.httpsRequired", await response.ReadProblemCodeAsync(Token));
        Assert.Empty(TestAccounts.Audit(factory.Services, "token.create"));
    }

    [Theory]
    [InlineData("Bearer invalid")]
    [InlineData("Basic invalid")]
    [InlineData("Bearer")]
    public async Task AuthorizationNeverFallsBackToAnOtherwiseValidAdministratorCookie(string authorization)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var response = await admin.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActuallyPresentEmptyAuthorizationHeaderDoesNotFallBackToCookie()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var setup = await admin.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var cookie = string.Join("; ", setup.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        // HttpClient suppresses an empty default Authorization value; inject the actual wire-level header collection instead.
        var context = await factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            context.Request.Path = Root;
            context.Request.Headers.Cookie = cookie;
            context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues("");
        }, Token);
        Assert.Equal(401, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        Assert.Contains("token.invalid", await reader.ReadToEndAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApiRequests.Setup)]
    [InlineData(ApiRequests.Login)]
    [InlineData(ApiRequests.Session)]
    [InlineData(ApiRequests.ChangePassword)]
    [InlineData(ApiRequests.Logout)]
    public async Task TokensCannotSetupLoginChangeCreatorPasswordOrObtainCreatorSessions(string path)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var created = await Create(admin, "automation", "administrator");
        using var script = Script(factory, created.Secret);
        for (var i = 0; i < 11; i++)
        {
            using var response = path == ApiRequests.Session ? await script.GetAsync(path, Token) : await script.PostJsonAsync(path, new { }, Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        // Forbidden token calls did not consume the Cookie login/password source-IP counter.
        using var login = await admin.LoginAsync("admin", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task HttpTokenUseIsRejectedWithoutRedirectEvenWithForgedForwardedProtocol()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var created = await Create(admin, "automation", "viewer");
        using var script = Script(factory, created.Secret, "http://localhost");
        script.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var response = await script.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("token.httpsRequired", await response.ReadProblemCodeAsync(Token));
        Assert.Null(response.Headers.Location);
        Assert.Null((await admin.GetJsonAsync(Root, Token)).GetProperty("items")[0].GetProperty("lastUsedAt").GetString());
    }

    [Fact]
    public async Task ExpiryAndIndependentFailureLimitUseTheServerClockAndDoNotConsumeLoginAttempts()
    {
        using var data = TemporaryDirectory.Create();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            // Only the token clock is controlled; real Host/network shutdown deadlines must keep advancing.
            services.RemoveAll<ApiTokenStore>();
            services.AddSingleton(provider => new ApiTokenStore(provider.GetRequiredService<ConfigDatabase>(),
                provider.GetRequiredService<SnowflakeIdGenerator>(), provider.GetRequiredService<AuditTrail>(), clock));
            services.RemoveAll<ApiTokenFailures>();
            services.AddSingleton(new ApiTokenFailures(clock));
        });
        using var admin = await Admin(factory);
        using var response = await admin.PostJsonAsync(Root, new { name = "expires", role = "viewer", expiresAt = clock.GetUtcNow().AddSeconds(1) }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var script = Script(factory, (await response.ReadJsonAsync(Token)).GetProperty("secret").GetString()!);
        using (var valid = await script.GetAsync("/api/edge-admin/v1/connections", Token))
        { Assert.Equal(HttpStatusCode.OK, valid.StatusCode); }
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
        { using var denied = await script.GetAsync("/api/edge-admin/v1/connections", Token); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        using var limited = await script.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("token.rateLimited", await limited.ReadProblemCodeAsync(Token));
        Assert.NotNull(limited.Headers.RetryAfter);
        using var login = await admin.LoginAsync("admin", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var expired = await script.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task HashOnlyStorageAndAuditAreAtomicAndTokensWorkAfterRealHostReopen()
    {
        using var data = TemporaryDirectory.Create();
        string secret;
        long id;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var admin = await Admin(factory);
            (id, secret) = await Create(admin, "persisted", "viewer");
            var db = factory.Services.GetRequiredService<ConfigDatabase>();
            var row = db.Client.Queryable<ApiTokenRecord>().InSingle(id);
            Assert.Equal(32, row.SecretHash.Length);
            Assert.Equal(ApiTokenSecrets.Hash(secret), row.SecretHash);
            Assert.DoesNotContain(secret, row.ToString(), StringComparison.Ordinal);
            db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_token_audit BEFORE INSERT ON audit_log WHEN NEW.action='token.revoke' BEGIN SELECT abs(-9223372036854775808); END;");
            using var failed = await admin.PostJsonAsync($"{Root}/{id}/revoke", new { confirmed = true }, Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Null(db.Client.Queryable<ApiTokenRecord>().InSingle(id).RevokedAt);
            Assert.Empty(TestAccounts.Audit(factory.Services, "token.revoke"));
            db.Client.Ado.ExecuteCommand("DROP TRIGGER fail_token_audit");
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var script = Script(restarted, secret);
        using var read = await script.GetAsync("/api/edge-admin/v1/connections", Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static async Task<HttpClient> Admin(TenonEdgeApplicationFactory factory, string address = "https://localhost")
    {
        var client = factory.CreateClient(new() { BaseAddress = new(address) });
        using var setup = await client.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        await client.UseSessionAsync(setup, Token);
        return client;
    }
    private static async Task<(long Id, string Secret)> Create(HttpClient admin, string name, string role)
    {
        using var response = await admin.PostJsonAsync(Root, new { name, role }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadJsonAsync(Token);
        return (result.GetProperty("token").GetProperty("id").GetInt64(), result.GetProperty("secret").GetString()!);
    }
    private static HttpClient Script(TenonEdgeApplicationFactory factory, string secret, string address = "https://localhost")
    {
        var client = factory.CreateClient(new() { BaseAddress = new(address), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}

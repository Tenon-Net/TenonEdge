using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class HttpCredentialTests
{
    private static readonly string[] SeedInputs = ["meter/sim"];
    private const string Path = "/api/edge-admin/v1/connections/1/http-credential";
    private const string Recipient = "http://127.0.0.1:1883/";
    private const string Secret = "fixture-password-not-real";

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RolePermissionsApplyAndResponsesDoNotExposeSecrets(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        await ReadyAsync(factory);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await PutAsync(client, new { recipient = Recipient, kind = "basic", username = "http-user", secret = Secret });
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        if (expected == HttpStatusCode.OK)
        {
            var metadata = await response.ReadJsonAsync(Token);
            Assert.True(metadata.GetProperty("configured").GetBoolean());
            Assert.False(metadata.TryGetProperty("secret", out _));
            Assert.False(metadata.TryGetProperty("protectedSecret", out _));
            var record = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<HttpCredentialRecord>().InSingle(1);
            Assert.NotEqual(Secret, record.ProtectedSecret);
            Assert.DoesNotContain(Secret, record.ProtectedSecret!, StringComparison.Ordinal);
            Assert.Equal(Secret, Store(factory).ResolveHttp(1, Recipient)!.Secret);
            Assert.Null(Store(factory).ResolveHttp(1, "http://127.0.0.1:1884/"));
            using var get = await client.GetAsync(new Uri(Path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.DoesNotContain(Secret, await get.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(TestAccounts.Audit(factory.Services, "credential.http.write")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnonymousAndMissingCsrfRequestsCannotWrite()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        await ReadyAsync(factory);
        using var anonymous = factory.CreateClient();
        using var denied = await PutAsync(anonymous, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await PutAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Null(Store(factory).Read(1));
    }

    [Fact]
    public async Task RecipientChangeInvalidatesByDefaultAndReturningToOldRecipientDoesNotRestoreIt()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        var changed = await WriteAsync(client, new { recipient = "https://localhost:8883/", expectedVersion = first.GetProperty("version").GetString() });
        Assert.False(changed.GetProperty("configured").GetBoolean());
        Assert.Equal("user", Store(factory).DescribeHttp(1)!.Username); // 作废秘密不能退化成匿名认证。
        Assert.Null(Store(factory).ResolveHttp(1, Recipient));
        var returned = await WriteAsync(client, new { recipient = Recipient, expectedVersion = changed.GetProperty("version").GetString() });
        Assert.False(returned.GetProperty("configured").GetBoolean());
        Assert.Null(Store(factory).ResolveHttp(1, Recipient));
        var audits = TestAccounts.Audit(factory.Services, "credential.http.write");
        Assert.Equal(3, audits.Count);
        Assert.Contains("invalidated=True", audits[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionIsExplicitAdministratorOnlyAndAudited()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await AdminAsync(factory);
        TestAccounts.Create(factory.Services, "config", ApiRequests.StrongPassword, UserRole.Configurator);
        using var config = await factory.CreateLoggedInClientAsync("config", Token);
        var first = await WriteAsync(admin, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        var body = new { recipient = "http://localhost:1884/", expectedVersion = first.GetProperty("version").GetString(), retainExisting = true };
        using var denied = await PutAsync(config, body);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(Secret, Store(factory).ResolveHttp(1, Recipient)!.Secret);
        var changed = await WriteAsync(admin, body);
        Assert.True(changed.GetProperty("configured").GetBoolean());
        Assert.Null(Store(factory).ResolveHttp(1, Recipient));
        Assert.Equal(Secret, Store(factory).ResolveHttp(1, body.recipient)!.Secret);
        Assert.Contains("retained=True", TestAccounts.Audit(factory.Services, "credential.http.write")[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleVersionsCannotOverwriteCredentials()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = "new-password", expectedVersion = first.GetProperty("version").GetString() });
        using var conflict = await PutAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = "stale", expectedVersion = first.GetProperty("version").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("credential.versionConflict", await conflict.ReadProblemCodeAsync(Token));
        Assert.Equal("new-password", Store(factory).ResolveHttp(1, Recipient)!.Secret);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrFailedRecordsRequireAnAccountDeclarationAndChangedAccountGetsANewMarker(bool failed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        var marker = Store(factory).ResolveHttp(1, Recipient)!.Identity.AccountMarker;
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            Encoding.UTF8.GetBytes("{}"), "test-fingerprint", "test", "test", 1)], Token)).Committed);
        if (failed)
        {
            var seq = Assert.Single(cache.ReadPending(1, 0, 10)).Seq;
            Assert.True((await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), Token)).Committed);
        }

        var version = first.GetProperty("version").GetString();
        using var missing = await PutAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = "new-password", expectedVersion = version });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("credential.accountDeclarationRequired", await missing.ReadProblemCodeAsync(Token));
        var same = await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = "new-password", expectedVersion = version, accountChanged = false });
        Assert.Equal(marker, Store(factory).ResolveHttp(1, Recipient)!.Identity.AccountMarker);
        await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = "other-account", expectedVersion = same.GetProperty("version").GetString(), accountChanged = true });
        Assert.NotEqual(marker, Store(factory).ResolveHttp(1, Recipient)!.Identity.AccountMarker);
        Assert.Single(failed ? cache.ReadFailed(1, 0, 10) : cache.ReadPending(1, 0, 10));
    }

    [Theory]
    [InlineData("http://user:secret@localhost:1883")]
    [InlineData("http://localhost:0")]
    [InlineData("http://localhost:1883?password=secret")]
    [InlineData("http://localhost:1883#fragment")]
    [InlineData("mqtt://localhost:1883")]
    public async Task RecipientBindingRejectsSecretsAndUnsupportedUrlComponents(string recipient)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        using var response = await PutAsync(client, new { recipient, kind = "basic", username = "user", secret = Secret });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Store(factory).Read(1));
    }

    [Fact]
    public async Task CredentialsSurviveRestartButCannotBeDecryptedWithAnotherPurposeOrConnectionId()
    {
        using var data = TemporaryDirectory.Create();
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = await AdminAsync(factory);
            await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
            var record = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<HttpCredentialRecord>().InSingle(1);
            var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => provider.CreateProtector(SecretProtection.Purposes.HttpCredential, "2").Unprotect(record.ProtectedSecret!));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => provider.CreateProtector(SecretProtection.Purposes.HttpsCertificate).Unprotect(record.ProtectedSecret!));
        }

        await using (var restarted = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            await ReadyAsync(restarted);
            Assert.Equal(Secret, Store(restarted).ResolveHttp(1, Recipient)!.Secret);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SeedCommitInvalidatesChangedRecipientsAtomicallyWithItsAudit(bool failAudit, bool input)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        await WriteAsync(client, new { recipient = Recipient, kind = "basic", username = "user", secret = Secret });
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var configuration = input
            ? JsonSerializer.SerializeToElement(new { connectionId = 1, baseAddress = "http://localhost:1884/", fields = new[] { new { key = "value", path = "/value", type = "integer" } } })
            : JsonSerializer.SerializeToElement(new { connectionId = 1, baseAddress = "http://localhost:1884/", inputs = SeedInputs });
        var document = RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        { schemaVersion = 1, units = new[] { new { kind = input ? "input.http" : "output.http", key = input ? "meter/http" : "out", configuration } } }));
        var revision = versions.Freeze(document, RuntimeSnapshotOrigins.Seed, null, "test");
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        if (failAudit)
        {
            database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_credential_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.http.invalidate' BEGIN SELECT RAISE(ABORT, 'test-audit-fault'); END");
            Assert.ThrowsAny<Exception>(() => versions.Commit(revision, null));
            Assert.Equal(Secret, Store(factory).ResolveHttp(1, Recipient)!.Secret);
            Assert.Null(versions.ReadPointers().EffectiveRevision);
        }
        else
        {
            versions.Commit(revision, null);
            Assert.False(Store(factory).Read(1)!.Configured);
            Assert.Null(Store(factory).ResolveHttp(1, Recipient));
            Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
            Assert.Single(TestAccounts.Audit(factory.Services, "credential.http.invalidate"));
        }
    }

    [Fact]
    public void SeedParsingRejectsSecretBeforeAnySnapshotCanBeFrozen()
    {
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"units":[{"kind":"output.http","key":"out","configuration":{"connectionId":1,"baseAddress":"http://localhost:1884/","inputs":["meter/sim"],"secret":"never-store-this"}}]}
            """)));
    }

    [Theory]
    [InlineData("basic", "user", null)]
    [InlineData("bearer", null, null)]
    [InlineData("apiKeyHeader", null, "X-Api-Key")]
    [InlineData("apiKeyQuery", null, "api_key")]
    public async Task AllAuthenticationKindsRoundTripWithoutReturningSecrets(string kind, string? username, string? name)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        await WriteAsync(client, new { recipient = "HTTP://LOCALHOST:80/base", kind, username, name, secret = Secret });
        Assert.Equal("http://localhost/base/", Store(factory).Read(1)!.Recipient);
        var credential = Store(factory).ResolveHttp(1, "http://localhost/base/")!;
        Assert.Equal(Secret, credential.Secret);
        Assert.Equal(username, credential.Identity.Username);
        Assert.Equal(name, credential.Identity.Name);
        Assert.DoesNotContain(Secret, credential.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("basic", "bad:user", null, "secret")]
    [InlineData("basic", "user", "extra", "secret")]
    [InlineData("bearer", null, null, "contains spaces")]
    [InlineData("bearer", null, null, "a=b")]
    [InlineData("bearer", null, null, "")]
    [InlineData("bearer", "extra", null, "secret")]
    [InlineData("apiKeyHeader", null, "Authorization", "secret")]
    [InlineData("apiKeyHeader", null, "Host", "secret")]
    [InlineData("apiKeyHeader", null, "not a header", "secret")]
    [InlineData("apiKeyQuery", null, "", "secret")]
    [InlineData("apiKeyQuery", "extra", "key", "secret")]
    public async Task InvalidAuthenticationIsRejectedWithoutSecretEcho(string kind, string? username, string? name, string secret)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        using var response = await PutAsync(client, new { recipient = Recipient, kind, username, name, secret });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Store(factory).Read(1));
        if (secret.Length > 0)
        { Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentProtocolsCannotShareCredentialIdentity(bool httpFirst)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var mqtt = factory.Services.GetRequiredService<MqttCredentialStore>();
        var http = Store(factory);
        var mqttRequest = new MqttCredentialRequest("mqtt://localhost:1883", "user", Secret);
        var httpRequest = new HttpCredentialRequest(Recipient, TenonEdge.Abstractions.Contracts.HttpCredentialKind.Bearer, Secret: Secret);
        if (httpFirst)
        {
            http.Write(1, httpRequest, "admin", null, true);
            Assert.Equal("credential.protocolConflict", Assert.Throws<CredentialWriteException>(() => mqtt.Write(1, mqttRequest, "admin", null, true)).Code);
        }
        else
        {
            mqtt.Write(1, mqttRequest, "admin", null, true);
            Assert.Equal("credential.protocolConflict", Assert.Throws<CredentialWriteException>(() => http.Write(1, httpRequest, "admin", null, true)).Code);
        }
    }

    [Fact]
    public async Task FailedWriteAuditRollsBackSecretAndVersion()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, kind = "bearer", secret = Secret });
        var version = first.GetProperty("version").GetString();
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            "CREATE TRIGGER fail_http_write BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.http.write' BEGIN SELECT RAISE(ABORT, 'test-audit-fault'); END");
        Assert.ThrowsAny<Exception>(() => Store(factory).Write(1,
            new HttpCredentialRequest(Recipient, TenonEdge.Abstractions.Contracts.HttpCredentialKind.Bearer, Secret: "replacement", ExpectedVersion: version), "admin", null, true));
        Assert.Equal(version, Store(factory).Read(1)!.Version);
        Assert.Equal(Secret, Store(factory).ResolveHttp(1, Recipient)!.Secret);
        Assert.Single(TestAccounts.Audit(factory.Services, "credential.http.write"));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static HttpCredentialStore Store(TenonEdgeApplicationFactory factory) => factory.Services.GetRequiredService<HttpCredentialStore>();
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, object body) => client.PutAsJsonAsync(new Uri(Path, UriKind.Relative), body, Token);
    private static async Task<JsonElement> WriteAsync(HttpClient client, object body)
    {
        using var response = await PutAsync(client, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync(Token);
    }

    private static async Task<HttpClient> AdminAsync(TenonEdgeApplicationFactory factory)
    {
        await ReadyAsync(factory);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        return await factory.CreateLoggedInClientAsync("admin", Token);
    }

    private static async Task ReadyAsync(TenonEdgeApplicationFactory factory)
    {
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (cache.GetStatus().Store is null)
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}

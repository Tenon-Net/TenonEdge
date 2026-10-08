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

public sealed class MqttCredentialTests
{
    private const string Path = "/api/edge-admin/v1/connections/1/mqtt-credential";
    private const string Recipient = "mqtt://127.0.0.1:1883";
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
        using var response = await PutAsync(client, new { recipient = Recipient, username = "mqtt-user", password = Secret });
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        if (expected == HttpStatusCode.OK)
        {
            var metadata = await response.ReadJsonAsync(Token);
            Assert.True(metadata.GetProperty("configured").GetBoolean());
            Assert.False(metadata.TryGetProperty("password", out _));
            Assert.False(metadata.TryGetProperty("protectedPassword", out _));
            var record = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<MqttCredentialRecord>().InSingle(1);
            Assert.NotEqual(Secret, record.ProtectedPassword);
            Assert.DoesNotContain(Secret, record.ProtectedPassword!, StringComparison.Ordinal);
            Assert.Equal(Secret, Store(factory).ResolveMqtt(1, Recipient)!.Password);
            Assert.Null(Store(factory).ResolveMqtt(1, "mqtt://127.0.0.1:1884"));
            using var get = await client.GetAsync(new Uri(Path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.DoesNotContain(Secret, await get.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(TestAccounts.Audit(factory.Services, "credential.mqtt.write")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnonymousAndMissingCsrfRequestsCannotWrite()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        await ReadyAsync(factory);
        using var anonymous = factory.CreateClient();
        using var denied = await PutAsync(anonymous, new { recipient = Recipient, username = "user", password = Secret });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await PutAsync(client, new { recipient = Recipient, username = "user", password = Secret });
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Null(Store(factory).Read(1));
    }

    [Fact]
    public async Task RecipientChangeInvalidatesByDefaultAndReturningToOldRecipientDoesNotRestoreIt()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, username = "user", password = Secret });
        var changed = await WriteAsync(client, new { recipient = "mqtts://localhost:8883", expectedVersion = first.GetProperty("version").GetString() });
        Assert.False(changed.GetProperty("configured").GetBoolean());
        Assert.Null(Store(factory).ResolveMqtt(1, Recipient));
        var returned = await WriteAsync(client, new { recipient = Recipient, expectedVersion = changed.GetProperty("version").GetString() });
        Assert.False(returned.GetProperty("configured").GetBoolean());
        Assert.Null(Store(factory).ResolveMqtt(1, Recipient));
        var audits = TestAccounts.Audit(factory.Services, "credential.mqtt.write");
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
        var first = await WriteAsync(admin, new { recipient = Recipient, username = "user", password = Secret });
        var body = new { recipient = "mqtt://localhost:1884", expectedVersion = first.GetProperty("version").GetString(), retainExisting = true };
        using var denied = await PutAsync(config, body);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(Secret, Store(factory).ResolveMqtt(1, Recipient)!.Password);
        var changed = await WriteAsync(admin, body);
        Assert.True(changed.GetProperty("configured").GetBoolean());
        Assert.Null(Store(factory).ResolveMqtt(1, Recipient));
        Assert.Equal(Secret, Store(factory).ResolveMqtt(1, body.recipient)!.Password);
        Assert.Contains("retained=True", TestAccounts.Audit(factory.Services, "credential.mqtt.write")[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleVersionsCannotOverwriteCredentials()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, username = "user", password = Secret });
        await WriteAsync(client, new { recipient = Recipient, username = "user", password = "new-password", expectedVersion = first.GetProperty("version").GetString() });
        using var conflict = await PutAsync(client, new { recipient = Recipient, username = "user", password = "stale", expectedVersion = first.GetProperty("version").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("credential.versionConflict", await conflict.ReadProblemCodeAsync(Token));
        Assert.Equal("new-password", Store(factory).ResolveMqtt(1, Recipient)!.Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrFailedRecordsRequireAnAccountDeclarationAndChangedAccountGetsANewMarker(bool failed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var first = await WriteAsync(client, new { recipient = Recipient, username = "user", password = Secret });
        var marker = Store(factory).ResolveMqtt(1, Recipient)!.AccountMarker;
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            Encoding.UTF8.GetBytes("{}"), "test-fingerprint", "test", "test", 1)], Token)).Committed);
        if (failed)
        {
            var seq = Assert.Single(cache.ReadPending(1, 0, 10)).Seq;
            Assert.True((await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), Token)).Committed);
        }

        var version = first.GetProperty("version").GetString();
        using var missing = await PutAsync(client, new { recipient = Recipient, username = "user", password = "new-password", expectedVersion = version });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("credential.accountDeclarationRequired", await missing.ReadProblemCodeAsync(Token));
        var same = await WriteAsync(client, new { recipient = Recipient, username = "user", password = "new-password", expectedVersion = version, accountChanged = false });
        Assert.Equal(marker, Store(factory).ResolveMqtt(1, Recipient)!.AccountMarker);
        await WriteAsync(client, new { recipient = Recipient, username = "user", password = "other-account", expectedVersion = same.GetProperty("version").GetString(), accountChanged = true });
        Assert.NotEqual(marker, Store(factory).ResolveMqtt(1, Recipient)!.AccountMarker);
        Assert.Single(failed ? cache.ReadFailed(1, 0, 10) : cache.ReadPending(1, 0, 10));
    }

    [Theory]
    [InlineData("mqtt://user:secret@localhost:1883")]
    [InlineData("mqtt://localhost:1883/path")]
    [InlineData("mqtt://localhost:1883?password=secret")]
    [InlineData("mqtt://localhost:1883#fragment")]
    [InlineData("http://localhost:1883")]
    public async Task RecipientBindingRejectsSecretsAndUnsupportedUrlComponents(string recipient)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        using var response = await PutAsync(client, new { recipient, username = "user", password = Secret });
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
            await WriteAsync(client, new { recipient = Recipient, username = "user", password = Secret });
            var record = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<MqttCredentialRecord>().InSingle(1);
            var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => provider.CreateProtector(SecretProtection.Purposes.MqttCredential, "2").Unprotect(record.ProtectedPassword!));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => provider.CreateProtector(SecretProtection.Purposes.HttpsCertificate).Unprotect(record.ProtectedPassword!));
        }

        await using (var restarted = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            await ReadyAsync(restarted);
            Assert.Equal(Secret, Store(restarted).ResolveMqtt(1, Recipient)!.Password);
        }
    }

    [Theory]
    [InlineData(false, "output")]
    [InlineData(true, "output")]
    [InlineData(false, "input")]
    [InlineData(true, "input")]
    [InlineData(false, "shared")]
    [InlineData(true, "shared")]
    public async Task SeedCommitInvalidatesChangedRecipientsAtomicallyWithItsAudit(bool failAudit, string usage)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        await WriteAsync(client, new { recipient = Recipient, username = "user", password = Secret });
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        const string mqttInput = """{"kind":"input.mqtt","key":"meter/mqtt","configuration":{"connectionId":1,"host":"localhost","port":1884,"topicFilter":"input/value","fields":[{"key":"v","path":"/v","type":"integer"}]}}""";
        var units = usage switch
        {
            "input" => mqttInput,
            "shared" => mqttInput + "," + """{"kind":"output.mqtt","key":"out","configuration":{"connectionId":1,"host":"localhost","port":1884,"topic":"output/value","inputs":["meter/mqtt"]}}""",
            _ => """{"kind":"input.simulator","key":"meter/sim","configuration":{"periodMs":1000}},{"kind":"output.mqtt","key":"out","configuration":{"connectionId":1,"host":"localhost","port":1884,"topic":"test","inputs":["meter/sim"]}}""",
        };
        var document = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"units":[{{units}}]}"""));
        var revision = versions.Freeze(document, RuntimeSnapshotOrigins.Seed, null, "test");
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        if (failAudit)
        {
            database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_credential_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.mqtt.invalidate' BEGIN SELECT RAISE(ABORT, 'test-audit-fault'); END");
            Assert.ThrowsAny<Exception>(() => versions.Commit(revision, null));
            Assert.Equal(Secret, Store(factory).ResolveMqtt(1, Recipient)!.Password);
            Assert.Null(versions.ReadPointers().EffectiveRevision);
        }
        else
        {
            versions.Commit(revision, null);
            Assert.False(Store(factory).Read(1)!.Configured);
            Assert.Null(Store(factory).ResolveMqtt(1, Recipient));
            Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
            Assert.Single(TestAccounts.Audit(factory.Services, "credential.mqtt.invalidate"));
        }
    }

    [Fact]
    public void SeedParsingRejectsPasswordBeforeAnySnapshotCanBeFrozen()
    {
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"units":[{"kind":"output.mqtt","key":"out","configuration":{"connectionId":1,"host":"localhost","topic":"test","inputs":["meter/sim"],"password":"never-store-this"}}]}
            """)));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MqttCredentialStore Store(TenonEdgeApplicationFactory factory) => factory.Services.GetRequiredService<MqttCredentialStore>();
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

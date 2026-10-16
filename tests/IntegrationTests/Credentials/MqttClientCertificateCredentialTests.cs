using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Adapters;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class MqttClientCertificateCredentialTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Recipient = "mqtts://localhost:8883";
    private const string Path = "/api/edge-admin/v1/connections/1/mqtt-credential";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Theory]
    [InlineData("Viewer", false, HttpStatusCode.Forbidden)]
    [InlineData("Configurator", false, HttpStatusCode.OK)]
    [InlineData("Administrator", true, HttpStatusCode.OK)]
    public async Task UploadedPrivateMaterialIsProtectedAndNeverReturned(string role, bool pfx, HttpStatusCode status)
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        var upload = peer.Issue(pfx: pfx);
        await using var factory = new TenonEdgeApplicationFactory();
        await ReadyAsync(factory);
        TestAccounts.Create(factory.Services, "actor", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("actor", Token);
        using var response = await PutAsync(client, Request(upload));
        Assert.Equal(status, response.StatusCode);
        AssertSafe(await response.Content.ReadAsStringAsync(Token), upload);
        if (status != HttpStatusCode.OK)
        { return; }
        var credential = Store(factory).ResolveMqtt(1, Recipient)!;
        Assert.Equal(MqttCredentialKind.ClientCertificate, credential.Kind);
        Assert.Equal("CN=gateway-client", credential.CertificateSubject);
        Assert.Equal(string.Empty, credential.Username);
        using var material = MqttCredentialValidation.OpenCertificate(credential, DateTimeOffset.UtcNow);
        Assert.NotNull(material);
        using var db = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
        var record = db.Queryable<MqttCredentialRecord>().InSingle(1);
        AssertSafe(record.ProtectedPassword!, upload);
        Assert.DoesNotContain(credential.Password, record.ProtectedPassword!, StringComparison.Ordinal);
        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();
        Assert.Throws<CryptographicException>(() => protection.CreateProtector(SecretProtection.Purposes.MqttCredential, "2").Unprotect(record.ProtectedPassword!));
        Assert.Throws<CryptographicException>(() => protection.CreateProtector(SecretProtection.Purposes.HttpCredential, "1").Unprotect(record.ProtectedPassword!));
        Assert.Null(Store(factory).ResolveMqtt(1, "mqtts://localhost:8884"));
        using var get = await client.GetAsync(new Uri(Path, UriKind.Relative), Token);
        AssertSafe(await get.Content.ReadAsStringAsync(Token), upload);
        AssertSafe(JsonSerializer.Serialize(TestAccounts.Audit(factory.Services, "credential.mqtt.write")), upload);
        AssertSafe(credential.ToString(), upload);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var noCsrf = await PutAsync(client, Request(upload) with { ExpectedVersion = credential.Identity.Version });
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Equal(credential.Identity, Store(factory).DescribeMqtt(1));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("mixedSecret")]
    [InlineData("mixedUsername")]
    [InlineData("mixedKind")]
    [InlineData("rawSecret")]
    [InlineData("missing")]
    [InlineData("wrongPassword")]
    [InlineData("large")]
    [InlineData("mixedFormat")]
    public async Task InvalidOrMixedUploadsDoNotWriteOrEchoSecrets(string scenario)
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        var upload = peer.Issue(pfx: scenario == "wrongPassword");
        var request = Request(upload);
        request = scenario switch
        {
            "plain" => request with { Recipient = "mqtt://localhost:8883" },
            "mixedSecret" => request with { Password = "fixture-secret" },
            "mixedUsername" => request with { Username = "user" },
            "mixedKind" => request with { Kind = MqttCredentialKind.Password, Password = "fixture-secret" },
            "rawSecret" => request with { Certificate = null, Password = "fixture-secret" },
            "missing" => request with { Certificate = null },
            "wrongPassword" => request with { Certificate = upload with { Password = "fixture-wrong" } },
            "large" => request with { Certificate = new(Pkcs12: new string('A', 90000)) },
            "mixedFormat" => request with { Certificate = upload with { Pkcs12 = "fixture-invalid" } },
            _ => throw new InvalidOperationException(),
        };
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        using var response = await PutAsync(client, request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSafe(await response.Content.ReadAsStringAsync(Token), upload);
        Assert.Null(Store(factory).Read(1));
        Assert.Empty(TestAccounts.Audit(factory.Services, "credential.mqtt.write"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubjectRotationRequiresDeclarationAndChangedSubjectChangesFrozenIdentity(bool failed)
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var store = Store(factory);
        var first = await store.WriteAsync(1, Request(peer.Issue()), "admin", null, true, token: TestContext.Current.CancellationToken);
        var old = store.DescribeMqtt(1)!;
        var settings = new MqttOutputSettings { ConnectionId = 1, Host = "localhost", Port = 8883, Tls = true };
        var fingerprint = settings.Fingerprint(old);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), fingerprint, "fixture", "event", null)], Token)).Committed);
        if (failed)
        { Assert.True((await cache.RecordFailureAsync(new(Assert.Single(cache.ReadPending(1, 0, 1)).Seq, 0, "rejected", true, false), Token)).Committed); }
        var rotated = Request(peer.Issue()) with { ExpectedVersion = first.Version };
        var error = await Assert.ThrowsAsync<CredentialWriteException>(() => store.WriteAsync(1, rotated, "admin", null, true, token: TestContext.Current.CancellationToken));
        Assert.Equal("credential.accountDeclarationRequired", error.Code);
        var same = await store.WriteAsync(1, rotated with { AccountChanged = false }, "admin", null, true, token: TestContext.Current.CancellationToken);
        Assert.Equal(fingerprint, settings.Fingerprint(store.DescribeMqtt(1)));
        Assert.Equal(old.AccountMarker, store.DescribeMqtt(1)!.AccountMarker);
        var changed = await store.WriteAsync(1, Request(peer.Issue("different")) with { ExpectedVersion = same.Version }, "admin", null, true, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(fingerprint, settings.Fingerprint(store.DescribeMqtt(1)));
        Assert.Equal("CN=different", OutputRecipientIdentity.Mqtt(Recipient, true, store.DescribeMqtt(1)).ClientCertificateSubject);
        Assert.Equal(old.AccountMarker, store.DescribeMqtt(1)!.AccountMarker);
        await store.WriteAsync(1, Request(peer.Issue("different")) with { ExpectedVersion = changed.Version, AccountChanged = true }, "admin", null, true, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(old.AccountMarker, store.DescribeMqtt(1)!.AccountMarker);
        Assert.Single(failed ? cache.ReadFailed(1, 0, 10) : cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task BindingRetentionRollbackAndRestartPreserveOneCurrentAuthentication()
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        using var data = TemporaryDirectory.Create();
        string expectedVersion;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = await AdminAsync(factory);
            var store = Store(factory);
            var first = await store.WriteAsync(1, Request(peer.Issue()), "admin", null, true, token: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(1, new("mqtt://localhost:8883", ExpectedVersion: first.Version, RetainExisting: true), "admin", null, true, token: TestContext.Current.CancellationToken));
            Assert.Equal("credential.administratorRequired", (await Assert.ThrowsAsync<CredentialWriteException>(() =>
                store.WriteAsync(1, new("mqtts://localhost:8884", ExpectedVersion: first.Version, RetainExisting: true), "admin", null, false, token: TestContext.Current.CancellationToken))).Code);
            var retained = await store.WriteAsync(1, new("mqtts://localhost:8884", ExpectedVersion: first.Version, RetainExisting: true), "admin", null, true, token: TestContext.Current.CancellationToken);
            Assert.NotNull(store.ResolveMqtt(1, retained.Recipient));
            Assert.Null(store.ResolveMqtt(1, Recipient));
            var invalidated = await store.WriteAsync(1, new(Recipient, ExpectedVersion: retained.Version), "admin", null, true, token: TestContext.Current.CancellationToken);
            Assert.False(invalidated.Configured);
            Assert.Null(store.ResolveMqtt(1, Recipient));
            first = await store.WriteAsync(1, Request(peer.Issue(pfx: true)) with { ExpectedVersion = invalidated.Version }, "admin", null, true, token: TestContext.Current.CancellationToken);
            using var db = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
            db.Ado.ExecuteCommand("CREATE TRIGGER fail_certificate_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.mqtt.write' BEGIN SELECT RAISE(ABORT, 'fixture'); END");
            await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync(1, Request(peer.Issue("other")) with { ExpectedVersion = first.Version }, "admin", null, true, token: TestContext.Current.CancellationToken));
            Assert.Equal(first.Version, store.DescribeMqtt(1)!.Version);
            db.Ado.ExecuteCommand("DROP TRIGGER fail_certificate_audit");
            expectedVersion = first.Version;
        }
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            await ReadyAsync(factory);
            var store = Store(factory);
            using var reloaded = MqttCredentialValidation.OpenCertificate(store.ResolveMqtt(1, Recipient), DateTimeOffset.UtcNow);
            Assert.Equal("CN=gateway-client", reloaded!.Info.Subject);
            Assert.Equal(expectedVersion, store.DescribeMqtt(1)!.Version);
            await store.WriteAsync(1, new(Recipient, Username: "user", Password: "fixture-token", ExpectedVersion: expectedVersion), "admin", null, true, token: TestContext.Current.CancellationToken);
            Assert.Null(store.DescribeMqtt(1)!.CertificateSubject);
            Assert.Equal(MqttCredentialKind.Password, store.DescribeMqtt(1)!.Kind);
            Assert.Equal("fixture-token", store.ResolveMqtt(1, Recipient)!.Password);
        }
    }

    [Fact]
    public async Task ImportAccountDigestTracksSubjectButNotCertificateRotationAndRejectsStaleDraft()
    {
        await using var peer = await MutualTlsHttpPeer.StartAsync(true, Token);
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = drafts.Write(null, new(drafts.Read().Version, "mqtt", "MQTT", ConnectionKind.Mqtt, Mqtt: new("localhost", 8883, Tls: true)), "admin", null);
        var id = draft.Connection.Id;
        var store = Store(factory);
        var request = Request(peer.Issue()) with { ExpectedDraftVersion = draft.Version };
        var first = await store.WriteAsync(id, request, "admin", null, true, token: TestContext.Current.CancellationToken);
        var digest = Digest();
        var second = await store.WriteAsync(id, request with { Certificate = peer.Issue(), ExpectedVersion = first.Version }, "admin", null, true, token: TestContext.Current.CancellationToken);
        Assert.Equal(digest, Digest());
        await store.WriteAsync(id, request with { Certificate = peer.Issue("other"), ExpectedVersion = second.Version }, "admin", null, true, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(digest, Digest());
        await Assert.ThrowsAsync<CredentialWriteException>(() => store.WriteAsync(id, request with { ExpectedDraftVersion = "stale" }, "admin", null, true, token: TestContext.Current.CancellationToken));
        string Digest()
        {
            using var db = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
            return Assert.Single(ConfigurationCredentialMetadata.Read(db)).AccountIdentity;
        }
    }

    [Fact]
    public async Task PasswordImportIdentityKeepsTheLegacyNullKindProjection()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await AdminAsync(factory);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = drafts.Write(null, new(drafts.Read().Version, "mqtt", "MQTT", ConnectionKind.Mqtt,
            Mqtt: new("localhost", 8883, Tls: true)), "admin", null);
        var store = Store(factory);
        await store.WriteAsync(draft.Connection.Id, new(Recipient, "alice", "fixture-password",
            ExpectedDraftVersion: draft.Version), "admin", null, true, Token);
        var identity = store.DescribeMqtt(draft.Connection.Id)!;
        using var db = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient();
        Assert.Equal(ConfigurationCredentialMetadata.AccountDigest("mqtt", "alice", null, identity.AccountMarker),
            Assert.Single(ConfigurationCredentialMetadata.Read(db)).AccountIdentity);
    }

    [Fact]
    public void SchemaUpgradePreservesExistingPasswordIdentityAndIsIdempotent()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(value => value.Version <= 32).ToArray());
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO mqtt_credential (connection_id,recipient,username,protected_password,account_marker,version,updated_at) VALUES (1,'mqtts://localhost:8883','user','existing-ciphertext','marker','v1',1)");
        var migrations = ConfigMigrations.All.Where(value => value.Version <= 33).ToArray();
        Assert.Equal(33, store.CreateMigrator(migrations).Migrate());
        var record = store.Database.Client.Queryable<MqttCredentialRecord>().InSingle(1);
        Assert.Equal(0, record.Kind);
        Assert.Null(record.CertificateSubject);
        Assert.Equal("existing-ciphertext", record.ProtectedPassword);
        Assert.Equal("user", record.Username);
        Assert.Equal("marker", record.AccountMarker);
        Assert.Equal("v1", record.Version);
        Assert.Equal(33, store.CreateMigrator(migrations).Migrate());
        Assert.ThrowsAny<Exception>(() => store.Database.Client.Ado.ExecuteCommand("UPDATE mqtt_credential SET kind=1 WHERE connection_id=1"));
        Assert.ThrowsAny<Exception>(() => store.Database.Client.Ado.ExecuteCommand("UPDATE mqtt_credential SET kind=42 WHERE connection_id=1"));
    }

    [Fact]
    public void FailedUpgradeRollsBackBothColumnsAndPreservesCiphertext()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(value => value.Version <= 32).ToArray());
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO mqtt_credential (connection_id,recipient,username,protected_password,account_marker,version,updated_at) VALUES (1,'mqtts://localhost:8883','user','existing-ciphertext','a','v1',1)");
        var broken = new ConfigMigration(34, "fixture_failure", ["INSERT INTO missing_table VALUES (1)"]);
        Assert.Throws<TenonEdge.Host.Bootstrap.HostStartupException>(() => store.CreateMigrator([.. ConfigMigrations.All.Where(value => value.Version <= 33), broken]).Migrate());
        Assert.Equal(32, store.Database.Client.Ado.GetInt("SELECT MAX(version) FROM schema_migration"));
        Assert.Equal("existing-ciphertext", store.Database.Client.Ado.GetString("SELECT protected_password FROM mqtt_credential WHERE connection_id=1"));
        Assert.Equal(0, store.Database.Client.Ado.GetInt("SELECT count(*) FROM pragma_table_info('mqtt_credential') WHERE name IN ('kind','certificate_subject')"));
        Assert.Equal(33, store.CreateMigrator(ConfigMigrations.All.Where(value => value.Version <= 33).ToArray()).Migrate());
    }

    private static MqttCredentialRequest Request(ClientCertificateUpload upload) => new(Recipient, Kind: MqttCredentialKind.ClientCertificate, Certificate: upload);
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, MqttCredentialRequest request) => client.PutAsJsonAsync(new Uri(Path, UriKind.Relative), request, Json, Token);
    internal static MqttCredentialStore Store(TenonEdgeApplicationFactory factory) => factory.Services.GetRequiredService<MqttCredentialStore>();
    internal static async Task<HttpClient> AdminAsync(TenonEdgeApplicationFactory factory)
    {
        await ReadyAsync(factory);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        return await factory.CreateLoggedInClientAsync("admin", Token);
    }
    internal static async Task ReadyAsync(TenonEdgeApplicationFactory factory)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (factory.Services.GetRequiredService<DeliveryCache>().GetStatus().Store is null)
        { await Task.Delay(10, deadline.Token); }
    }
    private static void AssertSafe(string text, ClientCertificateUpload upload)
    {
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", text, StringComparison.Ordinal);
        if (upload.Pkcs12 is not null)
        { Assert.DoesNotContain(upload.Pkcs12, text, StringComparison.Ordinal); }
        if (upload.PrivateKeyPem is not null)
        { Assert.DoesNotContain(upload.PrivateKeyPem, text, StringComparison.Ordinal); }
    }
}

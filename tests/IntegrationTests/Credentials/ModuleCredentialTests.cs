using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed partial class ModuleCredentialTests
{
    internal const string Secret = "fixture-module-secret-A";
    private const string NextSecret = "fixture-module-secret-B";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static ConnectionModuleDefinition Definition { get; } = new("authwire", 1, "Authenticated wire",
        new("connection.authwire", ParameterScope.Connection,
        [new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 255 },
         new("port", "Port", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "1", MaximumJson = "65535" },
         new("timeout", "Timeout", ParameterType.Number),
         new("auth", "Secret", ParameterType.Credential) { Required = true }]), ["host", "port"]);
    private static FixedDataDirectoryApplicationFactory Host(string path) => new(path, configureServices: services => services.AddSingleton(Definition));
    internal static ConnectionDraftRequest Draft(string version = "0", int port = 123, string key = "wire") => new(version, key, key,
        ConnectionKind.Module, Module: new("authwire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port, timeout = 1000 })));
    private static ConnectionDraftResult Create(IServiceProvider services) => services.GetRequiredService<ConnectionDraftStore>().Write(null, Draft(), "fixture", null);
    private static string Endpoint(long id) => $"/api/edge-admin/v1/connections/{id}/module-credential";
    internal static ModuleCredentialRequest Material(string draftVersion, string? credentialVersion = null, string secret = Secret,
        bool? changed = null, string account = "wire-user") => new(draftVersion, credentialVersion, new() { ["auth"] = secret }, account, changed);

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task ApiRolesCsrfAndReadMetadataNeverExposeMaterials(string role)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var draft = Create(host.Services);
        var path = Endpoint(draft.Connection.Id);
        using var anonymous = host.CreateClient();
        using var anonymousRead = await anonymous.GetAsync(path, Token);
        using var anonymousWrite = await anonymous.PutAsJsonAsync(path, Material(draft.Version), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousWrite.StatusCode);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var csrf = client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var denied = await client.PutAsJsonAsync(path, Material(draft.Version), Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        using var response = await client.PutAsJsonAsync(path, Material(draft.Version), Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        var store = host.Services.GetRequiredService<ModuleCredentialStore>();
        if (role == "Viewer")
        { _ = await store.WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token); }
        using var read = await client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl!.NoStore);
        var metadata = await read.ReadJsonAsync(Token);
        Assert.True(metadata.GetProperty("configured").GetBoolean());
        Assert.True(metadata.GetProperty("matchesDraft").GetBoolean());
        Assert.Equal("auth", Assert.Single(metadata.GetProperty("configuredFields").EnumerateArray()).GetString());
        Assert.DoesNotContain(Secret, metadata.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(draft.Version, host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Equal(Secret, store.ResolveModule(draft.Connection.Module!.Snapshot(draft.Connection.Id))!.GetSecret("auth"));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(TestAccounts.Audit(host.Services, "credential.module.write")), StringComparison.Ordinal);
        using var clear = await client.DeleteAsync(path + $"?expectedDraftVersion={draft.Version}&expectedVersion={store.Read(draft.Connection.Id)!.Identity.Version}", Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent, clear.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("tooLong")]
    [InlineData("account")]
    [InlineData("mixed")]
    public async Task BadMaterialsAreRejectedWithoutSecretsInErrorsOrDatabase(string reason)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var draft = Create(host.Services);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var request = Material(draft.Version);
        request = reason switch
        {
            "unknown" => request with { Secrets = new() { ["fixture-secret-field"] = Secret } },
            "missing" => request with { Secrets = [] },
            "empty" => request with { Secrets = new() { ["auth"] = "" } },
            "tooLong" => request with { Secrets = new() { ["auth"] = new string('x', 65537) } },
            "account" => request with { Account = null },
            _ => request with { RetainExisting = true },
        };
        using var response = await client.PutAsJsonAsync(Endpoint(draft.Connection.Id), request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-field", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Null(host.Services.GetRequiredService<ModuleCredentialStore>().Read(draft.Connection.Id));
        Assert.Empty(TestAccounts.Audit(host.Services, "credential.module.write"));
    }

    [Fact]
    public async Task ProtectedMaterialsSurviveRestartAndCannotMoveToAnotherConnectionOrRecipient()
    {
        using var data = TemporaryDirectory.Create();
        ConnectionDraftResult draft;
        string encrypted;
        await using (var host = Host(data.Path))
        {
            draft = Create(host.Services);
            _ = await host.Services.GetRequiredService<ModuleCredentialStore>().WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token);
            encrypted = host.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<ModuleCredentialRecord>().InSingle(draft.Connection.Id).ProtectedSecrets!;
            Assert.DoesNotContain(Secret, encrypted, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(host.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token)), StringComparison.Ordinal);
        }
        await using var restarted = Host(data.Path);
        var store = restarted.Services.GetRequiredService<ModuleCredentialStore>();
        var original = draft.Connection.Module!.Snapshot(draft.Connection.Id);
        Assert.Equal(Secret, store.ResolveModule(original)!.GetSecret("auth"));
        var other = restarted.Services.GetRequiredService<ConnectionDraftStore>().Write(null, Draft(draft.Version, key: "other"), "fixture", null);
        var db = restarted.Services.GetRequiredService<ConfigDatabase>();
        var copied = db.Client.Queryable<ModuleCredentialRecord>().InSingle(draft.Connection.Id);
        copied.ConnectionId = other.Connection.Id;
        db.Client.Insertable(copied).ExecuteCommand();
        Assert.Null(store.ResolveModule(other.Connection.Module!.Snapshot(other.Connection.Id)));
        Assert.Null(store.ResolveModule(Draft(port: 124).Module!.Snapshot(draft.Connection.Id)));
        db.Client.Ado.ExecuteCommand("UPDATE module_credential SET protected_secrets = 'corrupt' WHERE connection_id = @id", new SqlSugar.SugarParameter("@id", draft.Connection.Id));
        Assert.False(store.Read(draft.Connection.Id)!.Configured);
        Assert.Null(store.ResolveModule(original));
        Assert.Throws<ArgumentException>(() => restarted.Services.GetRequiredService<ConnectionModuleCatalog>().ValidateForUse(original, store));
    }

    [Fact]
    public async Task AddressChangesInvalidateByDefaultAndOnlyAdministratorMayExplicitlyRetain()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = Create(host.Services);
        var store = host.Services.GetRequiredService<ModuleCredentialStore>();
        var first = await store.WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token);
        var changed = drafts.Write(draft.Connection.Id, Draft(draft.Version, 124), "fixture", null);
        var old = draft.Connection.Module!.Snapshot(draft.Connection.Id);
        var next = changed.Connection.Module!.Snapshot(draft.Connection.Id);
        Assert.NotNull(store.ResolveModule(old)); // Saving a draft does not change an active target.
        Assert.Null(store.ResolveModule(next));
        Assert.False(store.Read(draft.Connection.Id)!.MatchesDraft);
        var retain = new ModuleCredentialRequest(changed.Version, first.Identity.Version, RetainExisting: true);
        Assert.Equal("credential.administratorRequired", (await Assert.ThrowsAsync<CredentialWriteException>(() => store.WriteAsync(draft.Connection.Id, retain, "fixture", null, false, Token))).Code);
        var retained = await store.WriteAsync(draft.Connection.Id, retain, "fixture", null, true, Token);
        Assert.Equal(Secret, store.ResolveModule(next)!.GetSecret("auth"));
        Assert.Null(store.ResolveModule(old));
        var changedAgain = drafts.Write(draft.Connection.Id, Draft(changed.Version, 125), "fixture", null);
        var invalidated = await store.WriteAsync(draft.Connection.Id, new(changedAgain.Version, retained.Identity.Version), "fixture", null, false, Token);
        Assert.False(invalidated.Configured);
        Assert.Null(store.ResolveModule(next));
        Assert.Null(store.ResolveModule(changedAgain.Connection.Module!.Snapshot(draft.Connection.Id)));
    }

    [Fact]
    public async Task PendingIdentityRequiresAccountDeclarationAndWritesRespectVersionsAndAuditAtomicity()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var draft = Create(host.Services);
        var store = host.Services.GetRequiredService<ModuleCredentialStore>();
        var first = await store.WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token);
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        var frozen = new DeliveryDraft(Guid.NewGuid(), draft.Connection.Id, Guid.NewGuid(), "meter", 1, Encoding.UTF8.GetBytes("{}"), "fixture", "fixture", "fixture", 1);
        Assert.True((await cache.EnqueueAsync([frozen], Token)).Committed);
        Assert.Equal("credential.accountDeclarationRequired", (await Assert.ThrowsAsync<CredentialWriteException>(() =>
            store.WriteAsync(draft.Connection.Id, Material(draft.Version, first.Identity.Version, NextSecret), "fixture", null, true, Token))).Code);
        var rotated = await store.WriteAsync(draft.Connection.Id, Material(draft.Version, first.Identity.Version, NextSecret, false), "fixture", null, true, Token);
        Assert.Equal(first.Identity.AccountMarker, rotated.Identity.AccountMarker);
        var changed = await store.WriteAsync(draft.Connection.Id, Material(draft.Version, rotated.Identity.Version, Secret, true), "fixture", null, true, Token);
        Assert.NotEqual(rotated.Identity.AccountMarker, changed.Identity.AccountMarker);
        Assert.Equal(frozen.DeliveryId, Assert.Single(cache.ReadPending(draft.Connection.Id, 0, 1)).DeliveryId);
        Assert.Equal("credential.versionConflict", (await Assert.ThrowsAsync<CredentialWriteException>(() =>
            store.WriteAsync(draft.Connection.Id, Material(draft.Version, first.Identity.Version, NextSecret, false), "fixture", null, true, Token))).Code);
        Assert.Equal("credential.connectionReferenced", Assert.Throws<CredentialWriteException>(() => store.Clear(draft.Connection.Id, draft.Version, changed.Identity.Version, "fixture", null)).Code);
        var db = host.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_module_credential_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.module.write' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync(draft.Connection.Id, Material(draft.Version, changed.Identity.Version, NextSecret, false), "fixture", null, true, Token));
        Assert.Equal(changed.Identity.Version, store.Read(draft.Connection.Id)!.Identity.Version);
        Assert.Equal(Secret, store.ResolveModule(draft.Connection.Module!.Snapshot(draft.Connection.Id))!.GetSecret("auth"));
        Assert.Equal(draft.Version, host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableCredentialsContainOnlyAccountDigestAndRequireNewSecretAcrossHosts(bool replace)
    {
        using var sourceData = TemporaryDirectory.Create();
        using var targetData = TemporaryDirectory.Create();
        byte[] file;
        await using (var source = Host(sourceData.Path))
        {
            var draft = Create(source.Services);
            _ = await source.Services.GetRequiredService<ModuleCredentialStore>().WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token);
            file = source.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token);
            var same = source.Services.GetRequiredService<ConfigurationImportStore>().Prepare(file, ConfigurationImportMode.Merge, draft.Version, Token);
            Assert.Equal("preserved", Assert.Single(same.Credentials).Action);
        }
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(file), StringComparison.Ordinal);
        await using var target = Host(targetData.Path);
        var imports = target.Services.GetRequiredService<ConfigurationImportStore>();
        var candidate = imports.Prepare(file, replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge, "0", Token);
        Assert.Equal("requiresInput", Assert.Single(candidate.Credentials).Action);
        TestAccounts.Create(target.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var actor = TestAccounts.Find(target.Services, "operator")!;
        var saved = imports.Commit(candidate, new(actor.Id, actor.SecurityStamp, null), Token);
        var draftConnection = Assert.Single(candidate.Draft.Connections);
        Assert.Equal("configuration.credentialsRequired", Assert.Throws<DraftWriteException>(() => target.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(saved.Version, Token)).Code);
        var metadata = await target.Services.GetRequiredService<ModuleCredentialStore>().WriteAsync(draftConnection.Id, Material(saved.Version), "operator", null, false, Token);
        Assert.NotEqual(saved.Version, metadata.DraftVersion); // Completing an import's explicit draft marker is a real edit.
        Assert.True(target.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(metadata.DraftVersion, Token).PreflightPassed);
        _ = target.Services.GetRequiredService<ConnectionDraftStore>().Delete(draftConnection.Id, metadata.DraftVersion, "operator", null);
        Assert.Null(target.Services.GetRequiredService<ModuleCredentialStore>().Read(draftConnection.Id));
    }
}

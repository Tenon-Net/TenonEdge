using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed partial class ModuleCredentialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitInvalidatesChangedRecipientAtomicallyWithAudit(bool failAudit)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton(Definition);
            services.AddSingleton<IInputModuleFactory>(provider => new CredentialWireModule(provider.GetRequiredService<IModuleCredentialResolver>(),
                provider.GetRequiredService<ISharedConnections>(), provider.GetRequiredService<IOutboundConnector>()));
        });
        var draft = Create(host.Services);
        var credentials = host.Services.GetRequiredService<ModuleCredentialStore>();
        var before = await credentials.WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token);
        var connection = Draft(port: 124).Module!.Snapshot(draft.Connection.Id);
        var plan = RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new[] { new { kind = "input.authwire", key = "meter/wire", configuration = new
            { inputModule = new { version = 1, parameters = new { wire = draft.Connection.Id.ToString(CultureInfo.InvariantCulture) },
                connections = new { wire = new { connectionId = connection.ConnectionId, kind = connection.Kind, configuration = connection.Configuration } } } } } }
        }), inputs: host.Services.GetRequiredService<InputModuleCatalog>());
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var revision = versions.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "fixture");
        if (failAudit)
        {
            host.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
                "CREATE TRIGGER deny_module_invalidation BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.module.invalidate' BEGIN SELECT RAISE(ABORT, 'fixture'); END");
            Assert.ThrowsAny<Exception>(() => versions.Commit(revision, null));
            Assert.Equal(before.Identity, credentials.Read(draft.Connection.Id)!.Identity);
            Assert.Equal(Secret, credentials.ResolveModule(draft.Connection.Module!.Snapshot(draft.Connection.Id))!.GetSecret("auth"));
            Assert.Null(versions.ReadPointers().EffectiveRevision);
            Assert.Empty(TestAccounts.Audit(host.Services, "credential.module.invalidate"));
        }
        else
        {
            versions.Commit(revision, null);
            Assert.False(credentials.Read(draft.Connection.Id)!.Configured);
            Assert.Null(credentials.ResolveModule(connection));
            Assert.Null(credentials.ResolveModule(draft.Connection.Module!.Snapshot(draft.Connection.Id)));
            Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
            Assert.Single(TestAccounts.Audit(host.Services, "credential.module.invalidate"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationWaitIsCancellableAndBoundedWithoutHoldingConfigurationTransaction(bool timeout)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var draft = Create(host.Services);
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        var database = host.Services.GetRequiredService<ConfigDatabase>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new ModuleCredentialStore(database, host.Services.GetRequiredService<IDataProtectionProvider>(),
            host.Services.GetRequiredService<ConnectionModuleCatalog>(), cache, host.Services.GetRequiredService<AuditTrail>(), clock);
        var first = await store.WriteAsync(draft.Connection.Id, Material(draft.Version), "fixture", null, true, Token);
        using var publication = await cache.IdentityBarrier.EnterPublicationAsync([draft.Connection.Id], Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var writing = store.WriteAsync(draft.Connection.Id, Material(draft.Version, first.Identity.Version, NextSecret), "fixture", null, true, stop.Token);
        Assert.False(writing.IsCompleted);
        Assert.Equal(1, database.InTransaction(client => client.Ado.GetInt("SELECT 1")));
        if (timeout)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("credential.publicationBusy", (await Assert.ThrowsAsync<CredentialWriteException>(() => writing)).Code);
        }
        else
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        }
        Assert.Equal(first.Identity, store.Read(draft.Connection.Id)!.Identity);
        Assert.Single(TestAccounts.Audit(host.Services, "credential.module.write"));
        publication.Dispose();
        var next = await store.WriteAsync(draft.Connection.Id, Material(draft.Version, first.Identity.Version, NextSecret), "fixture", null, true, Token);
        Assert.NotEqual(first.Identity.Version, next.Identity.Version);
        Assert.Equal(first.Identity.AccountMarker, next.Identity.AccountMarker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingWriteFreezesCallerMaterialsAndRechecksDraftInsideTransaction(bool staleDraft)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var draft = Create(host.Services);
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        var store = host.Services.GetRequiredService<ModuleCredentialStore>();
        using var publication = await cache.IdentityBarrier.EnterPublicationAsync([draft.Connection.Id], Token);
        var request = Material(draft.Version);
        var writing = store.WriteAsync(draft.Connection.Id, request, "fixture", null, true, Token);
        Assert.False(writing.IsCompleted);
        request.Secrets!["auth"] = NextSecret;
        if (staleDraft)
        { _ = host.Services.GetRequiredService<ConnectionDraftStore>().Write(draft.Connection.Id, Draft(draft.Version, 124), "fixture", null); }
        publication.Dispose();
        if (staleDraft)
        {
            Assert.Equal("credential.draftConflict", (await Assert.ThrowsAsync<CredentialWriteException>(() => writing)).Code);
            Assert.Null(store.Read(draft.Connection.Id));
            Assert.Empty(TestAccounts.Audit(host.Services, "credential.module.write"));
        }
        else
        {
            _ = await writing;
            Assert.Equal(Secret, store.ResolveModule(draft.Connection.Module!.Snapshot(draft.Connection.Id))!.GetSecret("auth"));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("recipient")]
    public async Task RollbackRechecksCurrentModuleCredentialAtCommitInsteadOfRestoringSecrets(string damage)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var saved = await ApplyTwoVersions(host.Services);
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var frozen = versions.FreezeManualRollback(1, saved.Version, "fixture");
        var database = host.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand(damage switch
        {
            "missing" => "DELETE FROM module_credential",
            "corrupt" => "UPDATE module_credential SET protected_secrets = 'fixture-corrupt'",
            _ => "UPDATE module_credential SET recipient = 'sha256:fixture-different'",
        });
        var error = Assert.Throws<DraftWriteException>(() => versions.Commit(frozen.Revision, null, markRunning: true, replaceDraftVersion: saved.Version));
        Assert.Equal("configuration.rollbackCredentialMissing", error.Code);
        Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(saved.Version, host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        versions.Abandon(frozen.Revision, "fixture");
        Assert.Equal(error.Code, Assert.Throws<DraftWriteException>(() => versions.ReviewRollback(1, saved.Version, Token)).Code);
    }

    [Fact]
    public async Task RecoveryApprovalIsInvalidatedByModuleCredentialRotationAndBadMaterialCannotBeApproved()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var saved = await ApplyTwoVersions(host.Services);
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var database = host.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET content = 'fixture-corrupt' WHERE revision = 2");
        TestAccounts.Create(host.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var account = TestAccounts.Find(host.Services, "admin")!;
        var approved = new RuntimeRecoveryAuthorization(versions.ReadRecovery(1, saved.Version), [], new(account.Id, account.SecurityStamp, null));
        versions.ValidateRecovery(approved, requireDraftVersion: true);
        var credentials = host.Services.GetRequiredService<ModuleCredentialStore>();
        var previous = credentials.Read(saved.Connection.Id)!;
        _ = await credentials.WriteAsync(saved.Connection.Id, Material(saved.Version, previous.Identity.Version, NextSecret), "fixture", null, true, Token);
        Assert.Equal("configuration.reviewChanged", Assert.Throws<DraftWriteException>(() => versions.ValidateRecovery(approved, requireDraftVersion: true)).Code);
        database.Client.Ado.ExecuteCommand("UPDATE module_credential SET protected_secrets = 'fixture-corrupt'");
        Assert.Equal("configuration.rollbackCredentialMissing", Assert.Throws<DraftWriteException>(() => versions.ReadRecovery(1, saved.Version)).Code);
        Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
    }

    private static async Task<ConnectionDraftResult> ApplyTwoVersions(IServiceProvider services)
    {
        var first = Create(services);
        _ = await services.GetRequiredService<ModuleCredentialStore>().WriteAsync(first.Connection.Id, Material(first.Version), "fixture", null, true, Token);
        var coordinator = services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(first.Version, "fixture", null, Token)).Activated);
        var next = services.GetRequiredService<ConnectionDraftStore>().Write(first.Connection.Id,
            Draft(first.Version) with { Name = "Changed label" }, "fixture", null);
        Assert.True((await coordinator.ApplyDraftAsync(next.Version, "fixture", null, Token)).Activated);
        return next;
    }
}

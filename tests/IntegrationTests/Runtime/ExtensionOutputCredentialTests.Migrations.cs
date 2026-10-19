using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Credentials;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ExtensionOutputCredentialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountChangeQuarantinesOldBytesUntilAdministratorMigratesThemToAnotherActiveConnection(bool renamed)
    {
        using var data = TemporaryDirectory.Create();
        using var sourcePeer = Listen();
        using var targetPeer = Listen();
        using var samples = new SemaphoreSlim(0);
        var sourcePort = ((IPEndPoint)sourcePeer.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)targetPeer.LocalEndpoint).Port;
        await using var host = Host(data.Path, samples, sourcePort, targetPort);
        var seed = Seed(host.Services, sourcePort);
        var target = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            ModuleCredentialTests.Draft(seed.Version, targetPort, "target"), "fixture", null);
        seed = seed with { Version = target.Version };
        TestAccounts.Create(host.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await host.CreateLoggedInClientAsync("admin", Token);
        var first = await WriteCredential(admin, seed, null, FirstSecret);
        _ = await WriteCredential(admin, new(seed.Version, target.Connection.Id), null, NextSecret, account: "target-user");
        var paused = await Control(admin, target.Connection.Id, true);
        Assert.Equal(1, await Apply(admin, host.Services, seed.Version));
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        samples.Release();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var sourceSocket = await sourcePeer.AcceptTcpClientAsync(deadline.Token);
        var before = await ReadFrame(sourceSocket.GetStream(), deadline.Token);
        var original = Assert.Single(runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10));
        await sourceSocket.GetStream().WriteAsync(new byte[] { 21 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().Single(item => item.ConnectionId == seed.ConnectionId).Sender.State == "automaticallyPaused");
        var changed = await WriteCredential(admin, seed, first.Identity.Version, "fixture-authwire-new-owner",
            changed: renamed ? null : true, account: renamed ? "other-user" : "wire-user");
        Assert.Equal(renamed, first.Identity.AccountMarker == changed.Identity.AccountMarker);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10).Count == 1);
        var isolated = Assert.Single(runtime.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10));
        Assert.Equal(DeliveryQuarantineReasons.TargetChanged, isolated.QuarantineReason);
        Assert.Equal(original.DeliveryId, isolated.DeliveryId);
        Assert.Equal(original.Fingerprint, isolated.Fingerprint);
        Assert.Equal(original.Payload.ToArray(), isolated.Payload.ToArray());
        Assert.Equal(0, sourceSocket.Available);
        samples.Release();
        var fresh = await ReadFrame(sourceSocket.GetStream(), deadline.Token);
        Assert.Equal("fixture-authwire-new-owner", fresh.Secret);
        using (var parsed = JsonDocument.Parse(fresh.Payload))
        { Assert.NotEqual(original.DeliveryId, parsed.RootElement.GetProperty("messageId").GetGuid()); }
        await sourceSocket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10).Count == 0);
        Assert.Single(runtime.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10));
        Assert.False(targetPeer.Pending());

        var selected = await admin.GetJsonAsync(Api + $"/deliveries/migrations/records?connectionId={seed.ConnectionId}&state=quarantined", Token);
        var item = Assert.Single(selected.GetProperty("items").EnumerateArray());
        var request = new DeliveryMigrationPrepareRequest(seed.ConnectionId, target.Connection.Id,
            [new(item.GetProperty("sequence").GetInt64(), item.GetProperty("deliveryId").GetGuid(), null)]);
        foreach (var role in new[] { UserRole.Configurator, UserRole.Viewer })
        {
            TestAccounts.Create(host.Services, role.ToString(), ApiRequests.StrongPassword, role);
            using var restricted = await host.CreateLoggedInClientAsync(role.ToString(), Token);
            using var denied = await restricted.PostJsonAsync(Api + "/deliveries/migrations/prepare", request, Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var prepared = await admin.PostJsonAsync(Api + "/deliveries/migrations/prepare", request, Token);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var review = await prepared.ReadJsonAsync(Token);
        Assert.True(review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal("authwire", review.GetProperty("target").GetProperty("protocol").GetString());
        Assert.Equal("target-user", review.GetProperty("target").GetProperty("account").GetString());
        Assert.Equal("wire-user", Assert.Single(review.GetProperty("sources").EnumerateArray()).GetProperty("recipient").GetProperty("account").GetString());
        Assert.Equal(1, review.GetProperty("quarantined").GetInt32());
        var id = review.GetProperty("id").GetGuid();
        using var unconfirmed = await admin.PostJsonAsync(Api + $"/deliveries/migrations/{id}/confirm", new DeliveryMigrationConfirmation(false), Token);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Single(runtime.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10));
        using var confirmed = await admin.PostJsonAsync(Api + $"/deliveries/migrations/{id}/confirm", new DeliveryMigrationConfirmation(true), Token);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(1, (await confirmed.ReadJsonAsync(Token)).GetProperty("migratedRecords").GetInt32());
        var migrated = Assert.Single(runtime.Deliveries.ReadPending(target.Connection.Id, 0, 10));
        AssertFrozen(original, migrated);
        Assert.NotEqual(original.Fingerprint, migrated.Fingerprint);
        Assert.Empty(runtime.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10));
        Assert.False(targetPeer.Pending()); // Migration must not override target's manual pause.
        Assert.Equal("completed", (await admin.GetJsonAsync(Api + $"/deliveries/migrations/{id}", Token)).GetProperty("status").GetString());
        _ = await Control(admin, target.Connection.Id, false, paused.ResumeVersion);
        using var targetSocket = await targetPeer.AcceptTcpClientAsync(deadline.Token);
        var delivered = await ReadFrame(targetSocket.GetStream(), deadline.Token);
        Assert.Equal(NextSecret, delivered.Secret);
        Assert.Equal(before.Address, delivered.Address);
        Assert.Equal(before.Payload, delivered.Payload);
        await targetSocket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(target.Connection.Id, 0, 10).Count == 0);
        Assert.Equal(0, sourceSocket.Available);
        Assert.Equal(1, host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Equal(2, host.Services.GetRequiredService<TestWireOutputTransport>().Sessions);
        Assert.Single(TestAccounts.Audit(host.Services, "delivery.migrationFinished"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddressMigrationConfirmsOwnershipAndKeepsAuthenticatedFrozenQueueAcrossNormalHostRestart(bool retainExisting)
    {
        using var data = TemporaryDirectory.Create();
        using var oldPeer = Listen();
        using var nextPeer = Listen();
        using var samples = new SemaphoreSlim(0);
        var oldPort = ((IPEndPoint)oldPeer.LocalEndpoint).Port;
        var nextPort = ((IPEndPoint)nextPeer.LocalEndpoint).Port;
        Setup seed;
        StoredDelivery original;
        OutputControlState pause;
        Guid intentId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using (var host = Host(data.Path, samples, oldPort, nextPort))
        {
            seed = Seed(host.Services, oldPort);
            TestAccounts.Create(host.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var admin = await host.CreateLoggedInClientAsync("admin", Token);
            var first = await WriteCredential(admin, seed, null, FirstSecret);
            Assert.Equal(1, await Apply(admin, host.Services, seed.Version));
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            samples.Release();
            using var socket = await oldPeer.AcceptTcpClientAsync(deadline.Token);
            var before = await ReadFrame(socket.GetStream(), deadline.Token);
            original = Assert.Single(runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10));
            Assert.Equal(original.Payload.ToArray(), before.Payload);
            await socket.GetStream().WriteAsync(new byte[] { 21 }, deadline.Token);
            await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().Single().Sender.State == "automaticallyPaused");
            pause = await Control(admin, seed.ConnectionId, true);
            await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().Single().Sender.State == "manuallyPaused");
            using var changed = await admin.PutAsJsonAsync(Api + $"/connections/{seed.ConnectionId}", ModuleCredentialTests.Draft(seed.Version, nextPort), Token);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            seed = seed with { Version = (await changed.ReadJsonAsync(Token)).GetProperty("version").GetString()! };
            var stale = await admin.GetJsonAsync(CredentialPath(seed.ConnectionId), Token);
            Assert.False(stale.GetProperty("matchesDraft").GetBoolean());
            if (retainExisting)
            {
                TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
                using var editor = await host.CreateLoggedInClientAsync("editor", Token);
                var retention = new ModuleCredentialRequest(seed.Version, first.Identity.Version, RetainExisting: true);
                using var denied = await editor.PutAsJsonAsync(CredentialPath(seed.ConnectionId), retention, Token);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                using var retained = await admin.PutAsJsonAsync(CredentialPath(seed.ConnectionId), retention, Token);
                Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            }
            else
            { _ = await WriteCredential(admin, seed, first.Identity.Version, NextSecret, changed: false); }
            using var ordinary = await admin.PostJsonAsync(Api + "/configuration/review", new ConfigurationReviewRequest(seed.Version), Token);
            Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
            var ordinaryReview = await ordinary.ReadJsonAsync(Token);
            Assert.False(ordinaryReview.GetProperty("preflightPassed").GetBoolean());
            Assert.Single(ordinaryReview.GetProperty("targetBlocks").EnumerateArray());
            using var prepared = await admin.PostJsonAsync(Api + "/configuration/migrations/prepare",
                new ConfigurationMigrationPrepareRequest(seed.Version, [seed.ConnectionId]), Token);
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            var review = await prepared.ReadJsonAsync(Token);
            Assert.True(review.GetProperty("canConfirm").GetBoolean());
            var target = Assert.Single(review.GetProperty("targets").EnumerateArray());
            intentId = target.GetProperty("intentId").GetGuid();
            Assert.Equal($"authwire://127.0.0.1:{oldPort}", target.GetProperty("source").GetProperty("address").GetString());
            Assert.Equal($"authwire://127.0.0.1:{nextPort}", target.GetProperty("target").GetProperty("address").GetString());
            Assert.Equal(1, target.GetProperty("count").GetInt32());
            Assert.False(nextPeer.Pending());
            Assert.Equal(0, socket.Available);
            var operation = review.GetProperty("id").GetGuid();
            using var missingConfirmation = await admin.PostJsonAsync(Api + $"/configuration/migrations/{operation}/confirm",
                new ConfigurationMigrationConfirmation(true, false), Token);
            Assert.Equal(HttpStatusCode.BadRequest, missingConfirmation.StatusCode);
            using var confirmed = await admin.PostJsonAsync(Api + $"/configuration/migrations/{operation}/confirm",
                new ConfigurationMigrationConfirmation(true, true), Token);
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
            var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(operation)?.FinishedAt is not null);
            var applied = await admin.GetJsonAsync(Api + $"/configuration/operations/{operation}", Token);
            Assert.True(applied.GetProperty("stage").GetString() == "completed", applied.GetRawText());
            Assert.Equal(2, applied.GetProperty("revision").GetInt64());
            var migrations = host.Services.GetRequiredService<DeliveryMigrationStore>();
            await OutputPipelineTests.UntilAsync(() => migrations.ReadIntent(intentId)?.Status == "completed");
            Assert.Equal(1, runtime.Deliveries.ReadMigrationHold(intentId)!.HeldRecords);
            var migrated = Assert.Single(runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10));
            AssertFrozen(original, migrated);
            Assert.NotEqual(original.Fingerprint, migrated.Fingerprint);
            Assert.False(nextPeer.Pending());
            Assert.True((await admin.GetJsonAsync(Api + $"/connections/{seed.ConnectionId}/delivery-control", Token)).GetProperty("manuallyPaused").GetBoolean());
            Assert.Single(TestAccounts.Audit(host.Services, "delivery.migrationIntentRegistered"));
            Assert.Single(TestAccounts.Audit(host.Services, "delivery.migrationFinished"));
        }
        await using var restarted = Host(data.Path, samples, oldPort, nextPort);
        using var nextAdmin = await restarted.CreateLoggedInClientAsync("admin", Token);
        var active = restarted.Services.GetRequiredService<RuntimeHost>();
        Assert.Equal(2, restarted.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Equal("completed", (await nextAdmin.GetJsonAsync(Api + $"/configuration/migrations/intents/{intentId}", Token)).GetProperty("status").GetString());
        AssertFrozen(original, Assert.Single(active.Deliveries.ReadPending(seed.ConnectionId, 0, 10)));
        Assert.Equal(1, active.Deliveries.GetPersistentCounters()["manuallyMigratedRecords"]);
        Assert.False(nextPeer.Pending());
        _ = await Control(nextAdmin, seed.ConnectionId, false, pause.ResumeVersion);
        using var nextSocket = await nextPeer.AcceptTcpClientAsync(deadline.Token);
        var deliveredAfterRestart = await ReadFrame(nextSocket.GetStream(), deadline.Token);
        Assert.Equal(retainExisting ? FirstSecret : NextSecret, deliveredAfterRestart.Secret);
        Assert.Equal(original.TargetAddress, deliveredAfterRestart.Address);
        Assert.Equal(original.Payload.ToArray(), deliveredAfterRestart.Payload);
        await nextSocket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => active.Deliveries.ReadPending(seed.ConnectionId, 0, 10).Count == 0);
        Assert.False(oldPeer.Pending());
        Assert.Empty(active.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10));
    }

    private static void AssertFrozen(StoredDelivery before, StoredDelivery after)
    {
        Assert.Equal((before.Seq, before.DeliveryId, before.SourceMessageId, before.CreatedAt, before.TargetAddress),
            (after.Seq, after.DeliveryId, after.SourceMessageId, after.CreatedAt, after.TargetAddress));
        Assert.Equal((before.DeviceKey, before.RouteId, before.NodeLabel, before.Revision, before.QualityOfService, before.ProtocolMetadata),
            (after.DeviceKey, after.RouteId, after.NodeLabel, after.Revision, after.QualityOfService, after.ProtocolMetadata));
        Assert.Equal(before.Headers, after.Headers);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        using var original = JsonDocument.Parse(before.Payload);
        using var migrated = JsonDocument.Parse(after.Payload);
        Assert.Equal(before.DeliveryId, migrated.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(before.SourceMessageId, migrated.RootElement.GetProperty("sourceMessageId").GetGuid());
        Assert.Equal(original.RootElement.GetProperty("collectedAt").GetString(), migrated.RootElement.GetProperty("collectedAt").GetString());
    }
}

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryMigrationPreparationTests
{
    private const string Root = "/api/edge-admin/v1/deliveries/migrations";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DeliveryMigrationActor Actor = new("session-one", "admin", "192.0.2.1");

    [Fact]
    public async Task AdministratorPreparesEveryOriginalIdentityAndConfirmsFrozenMixedStatesExactlyOnce()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 3, withCredentials: true);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await cache.RecordFailureAsync(new(saved.Items[1].Sequence, 3, "rejected", true, false), Token);
        await cache.QuarantineAsync(saved.Items[2].Sequence, DeliveryQuarantineReasons.TargetChanged, Token);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var prepare = await admin.PostJsonAsync(Root + "/prepare", Request(saved), Token);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        var review = await prepare.ReadJsonAsync(Token);
        var id = review.GetProperty("id").GetGuid();
        Assert.True(review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(3, review.GetProperty("count").GetInt32());
        Assert.Equal(1, review.GetProperty("pending").GetInt32());
        Assert.Equal(1, review.GetProperty("failed").GetInt32());
        Assert.Equal(1, review.GetProperty("quarantined").GetInt32());
        var source = Assert.Single(review.GetProperty("sources").EnumerateArray()).GetProperty("recipient");
        Assert.Equal("http://original.test/", source.GetProperty("address").GetString());
        Assert.False(source.GetProperty("accountRecorded").GetBoolean()); // Old HTTP Basic had no username.
        Assert.False(source.GetProperty("clientCertificateRecorded").GetBoolean());
        Assert.Null(source.GetProperty("account").GetString());
        Assert.Equal("http://target.test/", review.GetProperty("target").GetProperty("address").GetString());
        Assert.Equal("confirmed-target-account", review.GetProperty("target").GetProperty("account").GetString());
        Assert.DoesNotContain("fixture-source", review.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-target", review.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-original-account", review.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
        Assert.Null(cache.ReadMigrationReceipt(id));
        Assert.Equal(id, (await admin.GetJsonAsync(Root + "/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
        using var unconfirmed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = false }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        // Preparation never expands to records arriving afterwards.
        await cache.EnqueueAsync([Draft(saved.Source)], Token);
        using var confirm = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(3, (await confirm.ReadJsonAsync(Token)).GetProperty("migratedRecords").GetInt32());
        Assert.Equal(2, cache.ReadPending(saved.Target, 0, 10).Count);
        Assert.Single(cache.ReadFailed(saved.Target, 0, 10));
        Assert.Single(cache.ReadPending(saved.Source, 0, 10));
        var moved = cache.ReadPending(saved.Target, 0, 10).Concat(cache.ReadFailed(saved.Target, 0, 10)).OrderBy(row => row.Seq).ToArray();
        Assert.Equal(saved.Items.Select(item => item.Sequence), moved.Select(row => row.Seq));
        Assert.Equal(saved.Items.Select(item => item.DeliveryId), moved.Select(row => row.DeliveryId));
        Assert.All(moved, row => { Assert.Equal("{\"frozen\":42}", System.Text.Encoding.UTF8.GetString(row.Payload.Span)); Assert.Equal("old/path", row.TargetAddress); });
        Assert.True(factory.Services.GetRequiredService<OutputControlStore>().Read(saved.Target).ManuallyPaused);
        var outcome = await admin.GetJsonAsync($"{Root}/{id}", Token);
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal(3, outcome.GetProperty("affectedRecords").GetInt32());
        using var replay = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
        Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationFinished"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    [InlineData("unknown")]
    public async Task EverySelectedIdentityIsReviewedEvenWhenTheFirstRecordHasTheExpectedProtocol(string second)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 1);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var identity = new OutputRecipientIdentity("http", "https://another-original.test/", "Basic", "original-account", true, "CN=original-client", true);
        await cache.EnqueueAsync([Draft(saved.Source, second switch
        {
            "http" => identity.Freeze(),
            "mqtt" => OutputRecipientIdentity.Mqtt("mqtt://original.test:1883", false, null).Freeze(),
            _ => "unrecognized-summary-must-not-escape",
        })], Token);
        var keys = cache.ReadPending(saved.Source, 0, 10).Select(row => new DeliveryMigrationKey(row.Seq, row.DeliveryId, row.MigrationId)).ToArray();
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.PostJsonAsync(Root + "/prepare", Request(saved) with { Items = keys }, Token);
        var body = await response.ReadJsonAsync(Token);
        if (second == "http")
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var sources = body.GetProperty("sources").EnumerateArray().ToArray();
            Assert.Equal(2, sources.Length);
            Assert.All(sources, source => Assert.Equal(1, source.GetProperty("count").GetInt32()));
            Assert.Null(sources[0].GetProperty("recipient").GetProperty("account").GetString());
            Assert.Equal(identity.Account, sources[1].GetProperty("recipient").GetProperty("account").GetString());
            Assert.Equal(identity.ClientCertificateSubject, sources[1].GetProperty("recipient").GetProperty("clientCertificateSubject").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(second == "mqtt" ? DeliveryMigrationErrors.ProtocolMismatch : "delivery.migrationOriginalIdentityUnknown", body.GetProperty("code").GetString());
            Assert.DoesNotContain("unrecognized-summary", body.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal(2, cache.ReadPending(saved.Source, 0, 10).Count);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
    }

    [Fact]
    public async Task MqttQueueOnlyTargetCanReceiveConfirmedFrozenRecordsWithoutCreatingAnotherSender()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var source = connections.Write(null, new("0", "source", "Source", ConnectionKind.Mqtt, Mqtt: new("source.test")), "admin", null);
        var target = connections.Write(null, new(source.Version, "target", "Target", ConnectionKind.Mqtt, Mqtt: new("target.test")), "admin", null);
        var controls = factory.Services.GetRequiredService<OutputControlStore>();
        controls.Write(source.Connection.Id, new(true, true), "admin", null);
        controls.Write(target.Connection.Id, new(true, true), "admin", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(target.Version, "admin", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders() is { Length: 2 } senders &&
            senders.All(sender => sender.Sender.State == "manuallyPaused"));
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var draft = new DeliveryDraft(Guid.NewGuid(), source.Connection.Id, Guid.NewGuid(), "meter", 1, "mqtt-frozen"u8.ToArray(),
            "original-fingerprint", "mqtt://original.test:1883; authentication=password; account=\"original-account\"", "original/topic", 1);
        await cache.EnqueueAsync([draft], Token);
        var row = Assert.Single(cache.ReadPending(source.Connection.Id, 0, 10));
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.PostJsonAsync(Root + "/prepare", new DeliveryMigrationPrepareRequest(source.Connection.Id, target.Connection.Id,
            [new(row.Seq, row.DeliveryId, row.MigrationId)]), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = await response.ReadJsonAsync(Token);
        Assert.Equal("mqtt", review.GetProperty("target").GetProperty("protocol").GetString());
        Assert.Equal("original-account", review.GetProperty("sources")[0].GetProperty("recipient").GetProperty("account").GetString());
        using var confirm = await admin.PostJsonAsync($"{Root}/{review.GetProperty("id").GetGuid()}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var migrated = Assert.Single(cache.ReadPending(target.Connection.Id, 0, 10));
        Assert.Equal(row.Seq, migrated.Seq);
        Assert.Equal(row.DeliveryId, migrated.DeliveryId);
        Assert.Equal(row.SourceMessageId, migrated.SourceMessageId);
        Assert.Equal(row.CreatedAt, migrated.CreatedAt);
        Assert.Equal(row.TargetAddress, migrated.TargetAddress);
        Assert.Equal(row.QualityOfService, migrated.QualityOfService);
        Assert.True(row.Payload.Span.SequenceEqual(migrated.Payload.Span));
        Assert.Equal(2, coordinator.Runtime.GetOutputSenders().Length);
        Assert.True(controls.Read(target.Connection.Id).ManuallyPaused);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Anonymous")]
    public async Task MigrationMetadataPreparationConfirmationAndOutcomeAreAdministratorOnly(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 1);
        var id = factory.Services.GetRequiredService<DeliveryMigrationPreparation>().Prepare(Request(saved), Actor, Token).Id;
        using var client = role == "Anonymous" ? factory.CreateClient() : await factory.CreateLoggedInClientAsync(role.ToLowerInvariant(), Token);
        var expected = role == "Anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        using var records = await client.GetAsync($"{Root}/records?connectionId={saved.Source}&state=pending", Token);
        Assert.Equal(expected, records.StatusCode);
        using var prepare = await client.PostJsonAsync(Root + "/prepare", Request(saved), Token);
        Assert.Equal(expected, prepare.StatusCode);
        using var confirm = await client.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(expected, confirm.StatusCode);
        using var result = await client.GetAsync($"{Root}/{id}", Token);
        Assert.Equal(expected, result.StatusCode);
        using var current = await client.GetAsync(Root + "/preparation", Token);
        Assert.Equal(expected, current.StatusCode);
        using var cancel = await client.DeleteAsync($"{Root}/preparation/{id}", Token);
        Assert.Equal(expected, cancel.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
    }

    [Fact]
    public async Task SessionOwnershipCsrfAndCancellationDoNotLetAnotherLoginConsumePreparation()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 1);
        using var first = await factory.CreateLoggedInClientAsync("admin", Token);
        using var second = await factory.CreateLoggedInClientAsync("admin", Token);
        using var prepared = await first.PostJsonAsync(Root + "/prepare", Request(saved), Token);
        var id = (await prepared.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        using var forbidden = await second.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        using var overwrite = await second.PostJsonAsync(Root + "/prepare", Request(saved), Token);
        Assert.Equal(HttpStatusCode.Conflict, overwrite.StatusCode);
        using var otherCancel = await second.DeleteAsync($"{Root}/preparation/{id}", Token);
        Assert.Equal(HttpStatusCode.NotFound, otherCancel.StatusCode);
        var csrf = first.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        first.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var missingCsrf = await first.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        first.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        using var cancel = await first.DeleteAsync($"{Root}/preparation/{id}", Token);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        using var replay = await first.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
    }

    [Theory]
    [InlineData("mixedProtocol")]
    [InlineData("unknownSummary")]
    [InlineData("changedTarget")]
    [InlineData("changedRevision")]
    [InlineData("changedGeneration")]
    [InlineData("expired")]
    public async Task UnprovableProtocolAndChangedConfirmationBoundariesNeverMoveRecords(string change)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 1, change switch
        { "mixedProtocol" => OutputRecipientIdentity.Mqtt("mqtt://original.test:1883", false, null).Freeze(), "unknownSummary" => "unknown secret must not be echoed", _ => null });
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var clock = new FakeTimeProvider();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var preparation = new DeliveryMigrationPreparation(cache, factory.Services.GetRequiredService<RuntimeVersionStore>(), coordinator, clock);
        if (change is "mixedProtocol" or "unknownSummary")
        {
            var error = Assert.Throws<DeliveryMigrationRequestException>(() => preparation.Prepare(Request(saved), Actor, Token));
            Assert.Equal(change == "mixedProtocol" ? DeliveryMigrationErrors.ProtocolMismatch : "delivery.migrationOriginalIdentityUnknown", error.Code);
        }
        else
        {
            var review = preparation.Prepare(Request(saved), Actor, Token);
            if (change == "changedTarget")
            {
                factory.Services.GetRequiredService<HttpCredentialStore>().Write(saved.Target,
                    new("http://target.test/", HttpCredentialKind.Basic, Username: "different-owner", Secret: "test-only", ExpectedDraftVersion: saved.Version), "admin", null, true);
            }
            if (change == "changedRevision")
            { Assert.True((await coordinator.ApplyDraftAsync(saved.Version, "admin", null, Token)).Activated); }
            if (change == "expired")
            { clock.Advance(DeliveryMigrationPreparation.Lifetime); }
            if (change == "changedGeneration")
            {
                var old = Assert.Single(cache.ReadPending(saved.Source, 0, 10));
                Assert.True((await cache.MigrateAsync(DeliveryMigrationCommand.OldTarget(cache.Options.ConfigStoreId, Guid.NewGuid(), saved.Source,
                    old.Fingerprint, old.Fingerprint, old.FingerprintSummary))).Committed);
                var result = await preparation.Confirm(review.Id, true, Actor, Token);
                Assert.Equal(DeliveryMigrationErrors.SelectionChanged, result.Error);
            }
            else
            {
                var error = await Assert.ThrowsAsync<DeliveryMigrationRequestException>(() => preparation.Confirm(review.Id, true, Actor, Token));
                Assert.Equal(change == "expired" ? 404 : 409, error.StatusCode);
            }
        }
        Assert.Single(cache.ReadPending(saved.Source, 0, 10));
        Assert.Empty(cache.ReadPending(saved.Target, 0, 10));
    }

    [Fact]
    public async Task PreparationIsReadOnlyAndCannotUseDraftOnlyDisabledOrSameTarget()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 1);
        var manager = factory.Services.GetRequiredService<DeliveryMigrationPreparation>();
        Assert.Throws<ArgumentException>(() => manager.Prepare(Request(saved) with { TargetConnectionId = saved.Source }, Actor, Token));
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = connections.Write(null, new(saved.Version, "draft-only", "Draft only", ConnectionKind.Http, Http: new("http://draft.test/")), "admin", null);
        Assert.Throws<DeliveryMigrationRequestException>(() => manager.Prepare(Request(saved) with { TargetConnectionId = draft.Connection.Id }, Actor, Token));
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var listed = await admin.GetJsonAsync($"{Root}/records?connectionId={saved.Source}&state=pending", Token);
        Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("fingerprint", listed.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("frozen", listed.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var oldList = await admin.GetAsync($"/api/edge-admin/v1/deliveries?state=pending&connectionId={saved.Source}", Token);
        Assert.Equal(HttpStatusCode.BadRequest, oldList.StatusCode); // Existing Viewer-readable list has not been broadened.
    }

    [Fact]
    public async Task DisabledTargetIsNotReplacedWithItsDirtyEnabledDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await Seed(factory, 1);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var target = connections.Read().Connections.Single(connection => connection.Id == saved.Target);
        var disabled = connections.Write(target.Id, new(saved.Version, target.Key, target.Name, target.Kind, Enabled: false, Http: target.Http), "admin", null);
        Assert.True((await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(disabled.Version, "admin", null, Token)).Activated);
        connections.Write(target.Id, new(disabled.Version, target.Key, target.Name, target.Kind, Http: target.Http), "editor", null);
        var error = Assert.Throws<DeliveryMigrationRequestException>(() => factory.Services.GetRequiredService<DeliveryMigrationPreparation>().Prepare(Request(saved), Actor, Token));
        Assert.Equal(DeliveryMigrationErrors.Unavailable, error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaDeficitsAreVisibleBeforeConfirmationAndRecheckedAfterPreparation(bool fillsLater)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.RemoveAll<DeliveryCache>();
            services.AddSingleton(provider => new DeliveryCache(new()
            {
                DatabasePath = Path.Combine(provider.GetRequiredService<DataDirectoryLock>().Directory.Root, DeliveryCacheOptions.FileName),
                ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
                MaximumPendingPerTarget = 2,
            }, provider.GetRequiredService<TimeProvider>()));
        });
        var saved = await Seed(factory, 2);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        if (!fillsLater)
        { await cache.EnqueueAsync([Draft(saved.Target)], Token); }
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.PostJsonAsync(Root + "/prepare", Request(saved), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = await response.ReadJsonAsync(Token);
        Assert.Equal(fillsLater, review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(fillsLater ? 0 : 1, review.GetProperty("quota").GetProperty("pendingRecords").GetInt64());
        var id = review.GetProperty("id").GetGuid();
        if (fillsLater)
        { await cache.EnqueueAsync([Draft(saved.Target)], Token); }
        using var confirm = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        var problem = await confirm.ReadJsonAsync(Token);
        Assert.Equal(fillsLater ? DeliveryMigrationErrors.QuotaExceeded : "delivery.migrationNotConfirmable", problem.GetProperty("code").GetString());
        if (fillsLater)
        {
            Assert.Equal(1, problem.GetProperty("migrationQuota").GetProperty("pendingRecords").GetInt64());
            Assert.Equal("rejected", (await admin.GetJsonAsync($"{Root}/{id}", Token)).GetProperty("status").GetString());
        }
        Assert.Null(cache.ReadMigrationReceipt(id));
        Assert.Equal(2, cache.ReadPending(saved.Source, 0, 10).Count);
        Assert.Single(cache.ReadPending(saved.Target, 0, 10));
    }

    private static DeliveryMigrationPrepareRequest Request(Saved saved) => new(saved.Source, saved.Target, saved.Items);
    private static DeliveryDraft Draft(long source, string? summary = null) => new(Guid.NewGuid(), source, Guid.NewGuid(), "meter", 1,
        "{\"frozen\":42}"u8.ToArray(), "old-fingerprint", summary ?? "http://original.test/; authentication=Basic", "old/path",
        headers: new Dictionary<string, string> { ["X-Frozen"] = "original" }, protocolMetadata: "{\"version\":1,\"path\":\"old/path\",\"successPath\":null}");
    private static async Task<Saved> Seed(TenonEdgeApplicationFactory factory, int count, string? summary = null, bool withCredentials = false)
    {
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var source = connections.Write(null, new("0", "source", "Source", ConnectionKind.Http, Http: new("http://current-source.test/")), "admin", null);
        var target = connections.Write(null, new(source.Version, "target", "Target", ConnectionKind.Http, Http: new("http://target.test/")), "admin", null);
        if (withCredentials)
        {
            var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
            credentials.Write(source.Connection.Id, new("http://current-source.test/", HttpCredentialKind.Basic, Username: "not-the-original-account", Secret: "fixture-source", ExpectedDraftVersion: target.Version), "admin", null, true);
            credentials.Write(target.Connection.Id, new("http://target.test/", HttpCredentialKind.Basic, Username: "confirmed-target-account", Secret: "fixture-target", ExpectedDraftVersion: target.Version), "admin", null, true);
        }
        var controls = factory.Services.GetRequiredService<OutputControlStore>();
        controls.Write(source.Connection.Id, new(true, true), "admin", null);
        controls.Write(target.Connection.Id, new(true, true), "admin", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(target.Version, "admin", null, Token)).Activated);
        // 身份核对在手动暂停之前执行。先等原发送者完成初次核对，再构造三种区域；
        // 入库后才更换凭证会按产品规则隔离旧身份，不能用它作为混合区域测试的前提。
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders() is { Length: 2 } senders &&
            senders.All(sender => sender.Sender.State == "manuallyPaused"));
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        TestAccounts.Create(factory.Services, "configurator", ApiRequests.StrongPassword, UserRole.Configurator);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await cache.EnqueueAsync(Enumerable.Range(0, count).Select(_ => Draft(source.Connection.Id, summary)).ToArray(), Token);
        var items = cache.ReadPending(source.Connection.Id, 0, 1000).Select(row => new DeliveryMigrationKey(row.Seq, row.DeliveryId, row.MigrationId)).ToArray();
        Assert.Equal(count, items.Length);
        return new(source.Connection.Id, target.Connection.Id, target.Version, items);
    }
    private sealed record Saved(long Source, long Target, string Version, DeliveryMigrationKey[] Items);
}

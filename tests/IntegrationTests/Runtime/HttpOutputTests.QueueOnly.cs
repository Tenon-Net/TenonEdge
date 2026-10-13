using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Theory]
    [InlineData("compiled")]
    [InlineData("legacy")]
    [InlineData("retryLegacy")]
    public async Task UnroutedEffectiveTargetMigratesFrozenHttpAfterRestartRetryAndRollbackWithoutUsingDirtyDraft(string mode)
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var outputFactory = new HttpOutputFactory(new Credentials(), TimeProvider.System,
            new OutboundConnector(new LoopbackPolicy()), new SharedConnections());
        var deliveryId = Guid.NewGuid();
        var oldTarget = outputFactory.Build(new(RuntimeUnitId.Create("output.http", "old/path"), JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            baseAddress = "http://old.test/api/",
            path = "frozen/path?batch=7",
            inputs = Inputs,
            headers = new Dictionary<string, string> { ["X-Frozen"] = "original" },
            successPath = "/code",
            successValue = 42,
        })));
        var message = oldTarget.CreateMessage(Message(), deliveryId);
        long targetId;
        string historicalContent;
        string historicalHash;
        string rawPlanHash;
        string dirtyVersion;
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = bootstrap.CreateClient();
            using var setup = await client.CompleteSetupAsync(Token);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            var connections = bootstrap.Services.GetRequiredService<ConnectionDraftStore>();
            var saved = connections.Write(null, new("0", "queue-target", "Queue target", ConnectionKind.Http,
                Http: new(Base(server), Headers: new() { ["X-Not-Frozen"] = "must-not-be-added" })), "admin", null);
            targetId = saved.Connection.Id;
            var versions = bootstrap.Services.GetRequiredService<RuntimeVersionStore>();
            var routes = bootstrap.Services.GetRequiredService<RouteDraftStore>();
            var plan = mode == "compiled" ? RoutePlanCompiler.Compile(routes.ReadCompilation(saved.Version, Token), Token).Document
                : RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);
            rawPlanHash = plan.ContentHash;
            versions.Commit(versions.FreezeDraft(plan, saved.Version, "admin"), null);
            var frozen = versions.Load(1)!;
            historicalContent = frozen.Content;
            historicalHash = frozen.ContentHash;
            dirtyVersion = connections.Write(targetId, new(saved.Version, "queue-target", "Dirty", ConnectionKind.Http,
                Http: new("http://dirty.invalid/")), "editor", null).Version;
            if (mode == "retryLegacy")
            { bootstrap.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET abnormal_exit_count = 3 WHERE revision = 1"); }
            var cache = bootstrap.Services.GetRequiredService<DeliveryCache>();
            var queued = await cache.EnqueueAsync([new(deliveryId, 1, Guid.NewGuid(), "device", 1, message.Payload, message.Fingerprint,
                message.FingerprintSummary, message.Address, message.QualityOfService, message.Headers, protocolMetadata: message.ProtocolMetadata)], Token);
            Assert.True(queued.Committed, queued.Error);
        }
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) });
        using var started = host.CreateClient();
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        var store = host.Services.GetRequiredService<RuntimeVersionStore>();
        var execution = RuntimeSnapshotDocument.ReadExecutionPlan(store.Load(1)!);
        if (mode == "retryLegacy")
        {
            Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
            var retry = coordinator.BeginRetry(1, execution.ContentHash, "admin", null, Token);
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(retry.Id)?.FinishedAt is not null);
            Assert.True(coordinator.ReadApplication(retry.Id)!.Result!.Activated);
        }
        Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
        Assert.Equal(1, coordinator.Runtime.ActiveRevision);
        Assert.DoesNotContain(coordinator.Runtime.GetStatus().Units, unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal));
        Assert.Single(coordinator.Runtime.GetOutputSenders());
        Assert.Equal(historicalContent, store.Load(1)!.Content);
        Assert.Equal(historicalHash, store.Load(1)!.ContentHash);
        Assert.Equal(rawPlanHash, store.ReadVersion(1, Token).PlanHash);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        Assert.Equal("http://dirty.invalid/", Assert.Single(drafts.Read().Connections).Http!.BaseAddress);
        Assert.Equal(dirtyVersion, drafts.Read().Version);
        var cacheNow = host.Services.GetRequiredService<DeliveryCache>();
        await OutputPipelineTests.UntilAsync(() => cacheNow.ReadQuarantined(1, 0, 10).Count == 1);
        var original = Assert.Single(cacheNow.ReadQuarantined(1, 0, 10));
        Assert.Equal(DeliveryQuarantineReasons.TargetMissing, original.QuarantineReason);
        var identity = outputFactory.Build(Assert.Single(execution.Units)).CaptureIdentity()!;
        var command = DeliveryMigrationCommand.Selected(cacheNow.Options.ConfigStoreId, Guid.NewGuid(), 1, targetId,
            identity.Fingerprint, identity.Summary, [new(original.Seq, original.DeliveryId, original.Fingerprint, original.MigrationId)]);
        var received = ServeFrozenAsync();
        var moved = await coordinator.MigrateSelectedDeliveriesAsync(command, 1, original.FingerprintSummary, true, "admin", null, Token);
        Assert.True(moved.Committed, moved.Error);
        Assert.Equal(1, moved.AffectedRecords);
        CheckWire(await received.WaitAsync(TimeSpan.FromSeconds(15), Token));
        await OutputPipelineTests.UntilAsync(() => cacheNow.GetStatus().DeletedRecords == 1);
        Assert.Equal(0, cacheNow.GetStatus().CommittedRecords); // This process generated no new delivery.
        Assert.Equal(1, cacheNow.GetPersistentCounters()["committedRecords"]);
        Assert.Equal(1, cacheNow.ReadMigrationReceipt(command.OperationId)!.AffectedRecords);
        // 应用干净的下一版，再回滚旧完整检查点；回滚复用原快照，但执行视图仍有发送接续。
        var applied = await coordinator.ApplyDraftAsync(dirtyVersion, "admin", null, Token);
        Assert.True(applied.Activated, applied.Code);
        var review = store.ReviewRollback(1, drafts.Read().Version, Token);
        Assert.True(review.PreflightPassed);
        Assert.Equal(execution.ContentHash, review.PlanHash);
        var rolled = await coordinator.RollbackAsync(1, drafts.Read().Version, "admin", null, Token);
        Assert.True(rolled.Activated, rolled.Code);
        Assert.Equal(3, rolled.Revision);
        Assert.Equal(historicalContent, store.Load(3)!.Content);
        Assert.Equal(Base(server), Assert.Single(drafts.Read().Connections).Http!.BaseAddress);
        Assert.Single(coordinator.Runtime.GetOutputSenders());
        received = ServeFrozenAsync();
        await cacheNow.EnqueueAsync([new(deliveryId, targetId, Guid.NewGuid(), "device", 1, message.Payload, identity.Fingerprint,
            identity.Summary, message.Address, message.QualityOfService, message.Headers, protocolMetadata: message.ProtocolMetadata)], Token);
        CheckWire(await received.WaitAsync(TimeSpan.FromSeconds(15), Token));
        await OutputPipelineTests.UntilAsync(() => cacheNow.GetStatus().DeletedRecords == 2);

        async Task<(string Header, byte[] Body)> ServeFrozenAsync()
        {
            using var socket = await server.AcceptTcpClientAsync(Token);
            var wire = await ReadAsync(socket.GetStream());
            const string body = "{\"code\":42}";
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), Token);
            return wire;
        }
        void CheckWire((string Header, byte[] Body) wire)
        {
            Assert.Equal(message.Payload.ToArray(), wire.Body);
            Assert.Contains("POST /api/frozen/path?batch=7 ", wire.Header, StringComparison.Ordinal);
            Assert.Contains($"Idempotency-Key: {deliveryId:D}", wire.Header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("X-Frozen: original", wire.Header, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-Not-Frozen", wire.Header, StringComparison.OrdinalIgnoreCase);
        }
    }
}

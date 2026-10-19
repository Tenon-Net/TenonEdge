using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ConfigurationRecoveryTests
{
    [Fact]
    public async Task ExtensionRecoveryReviewsFrozenIdentityAndAddressBeforeExplicitActivation()
    {
        using var data = TemporaryDirectory.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        static void Register(IServiceCollection services)
        {
            services.AddSingleton(TestWireOutputModule.Definition);
            services.AddSingleton<IOutputModuleFactory, TestWireOutputModule>();
            services.AddSingleton<TestWireOutputTransport>();
            services.AddSingleton<IOutputAdapterFactory>(provider => provider.GetRequiredService<TestWireOutputTransport>());
        }
        long connectionId;
        await using (var initial = new FixedDataDirectoryApplicationFactory(data.Path, settings, Register))
        {
            TestAccounts.Create(initial.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
            var saved = initial.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new("0", "wire-target", "Wire", ConnectionKind.Module, Module: new("wire", 1,
                    JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port }))), "operator", null);
            connectionId = saved.Connection.Id;
            initial.Services.GetRequiredService<OutputControlStore>().Write(connectionId, new(true, true), "operator", null);
            var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.simulator", "meter/source"),
                JsonSerializer.SerializeToElement(new { periodMs = 1000, seed = 42 }));
            var output = new TestWireOutputModule().Unit(port, id: connectionId);
            var plan = RuntimePlanDocument.ParseStored(TestProtocolOutputModule.Document(input, output));
            var versions = initial.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(plan, saved.Version, "operator"), null);
            versions.Commit(versions.FreezeDraft(plan, saved.Version, "operator"), null);
        }
        new RuntimeStateReader(data.Path).Execute("UPDATE runtime_snapshot SET content='{}' WHERE revision=2");

        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, Register);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        var review = await Review(host, client);
        Assert.True(review.GetProperty("canConfirm").GetBoolean());
        var target = Assert.Single(review.GetProperty("targets").EnumerateArray());
        Assert.Equal(connectionId, target.GetProperty("connectionId").GetInt64());
        var identity = target.GetProperty("identity");
        Assert.Equal("wire", identity.GetProperty("recipient").GetProperty("protocol").GetString());
        Assert.Equal($"wire://127.0.0.1:{port}", identity.GetProperty("recipient").GetProperty("address").GetString());
        var fingerprint = identity.GetProperty("fingerprint").GetString()!;
        var queue = host.Services.GetRequiredService<DeliveryCache>();
        var draft = new DeliveryDraft(Guid.NewGuid(), connectionId, Guid.NewGuid(), "meter", 1,
            "{\"frozen\":9223372036854775807}"u8.ToArray(), fingerprint,
            OutputRecipientIdentity.Extension("wire", $"wire://127.0.0.1:{port}", "none", null).Freeze(), "events/meter");
        var sequence = Assert.Single((await queue.EnqueueAsync([draft], Token)).Seqs);
        var before = Assert.Single(queue.ReadPending(connectionId, 0, 10));
        Assert.True((await Review(host, client)).GetProperty("canConfirm").GetBoolean());
        await queue.RecordFailureAsync(new(sequence, 0, "rejected", true, false), Token);
        Assert.True((await Review(host, client)).GetProperty("canConfirm").GetBoolean());
        var wrongAddress = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), connectionId, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), fingerprint, draft.FingerprintSummary, "other/meter")], Token)).Seqs);
        Assert.False((await Review(host, client)).GetProperty("canConfirm").GetBoolean());
        using (var refused = await client.PostJsonAsync(Root + "/recover", Request(review), Token))
        { Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); }
        Assert.Empty(coordinator.ReadApplications());
        await queue.DeleteAsync([wrongAddress], Token);
        var wrongIdentity = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), connectionId, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), "different-account", draft.FingerprintSummary, "events/meter")], Token)).Seqs);
        Assert.False((await Review(host, client)).GetProperty("canConfirm").GetBoolean());
        await queue.QuarantineAsync(wrongIdentity, DeliveryQuarantineReasons.TargetChanged, Token);
        var approved = await Review(host, client);
        Assert.True(approved.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(0, transport.Adapters);
        Assert.Equal(0, transport.Sessions);
        Assert.Null(coordinator.Runtime.ActiveRevision);
        using var recovered = await client.PostJsonAsync(Root + "/recover", Request(approved), Token);
        Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
        var status = await Finished(host, client, recovered);
        Assert.True(status.GetProperty("result").GetProperty("activated").GetBoolean());
        Assert.Equal(3, coordinator.Runtime.ActiveRevision);
        var after = Assert.Single(queue.ReadFailed(connectionId, 0, 10));
        Assert.Equal(before.DeliveryId, after.DeliveryId);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal(before.TargetAddress, after.TargetAddress);
        Assert.Single(queue.ReadQuarantined(connectionId, 0, 10));
        Assert.True(host.Services.GetRequiredService<OutputControlStore>().Read(connectionId).ManuallyPaused);
        await OutputPipelineTests.UntilAsync(() => queue.ReadPending(connectionId, 0, 1).Count > 0);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders().SingleOrDefault()?.Sender.State == "manuallyPaused");
        Assert.False(server.Pending()); // A local lease is allowed; an actual TCP connection while paused is not.
    }
}

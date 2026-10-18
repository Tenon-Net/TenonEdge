using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputModuleProtectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Connection(string protocol, string host) => JsonSerializer.SerializeToElement(protocol == "http"
        ? (object)new { baseAddress = $"http://{host}.test/base/" } : new { host = host + ".test" });
    private static RuntimePlanDocument Plan(params RuntimeUnitPlan[] units) => RuntimePlanDocument.ParseStored(TestProtocolOutputModule.Document(units));

    [Theory]
    [InlineData("http", false)]
    [InlineData("mqtt", false)]
    [InlineData("http", true)]
    [InlineData("mqtt", true)]
    public async Task FrozenModuleTargetsCannotBypassDeletionAddressOrPathProtection(string protocol, bool failed)
    {
        using var data = TemporaryDirectory.Create();
        var module = new TestProtocolOutputModule(protocol);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services.AddSingleton<IOutputModuleFactory>(module));
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var original = module.Unit(Connection(protocol, "old"));
        versions.Commit(versions.Freeze(Plan(original), RuntimeSnapshotOrigins.Seed, null, "test"), null);
        var queue = host.Services.GetRequiredService<DeliveryCache>();
        var seq = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{\"exact\":9007199254740993}"u8.ToArray(), "fp", "summary", "send")], Token)).Seqs);
        if (failed)
        { await queue.RecordFailureAsync(new(seq, 0, "rejected", true, false), Token); }
        foreach (var next in new[] { Plan(), Plan(module.Unit(Connection(protocol, "new"))), Plan(module.Unit(Connection(protocol, "old"), "changed")) })
        {
            var calls = module.Calls;
            Assert.NotNull(versions.TargetChangeProblem(next));
            Assert.Equal(calls, module.Calls); // Frozen protection never runs third-party code.
            var revision = versions.Freeze(next, RuntimeSnapshotOrigins.Seed, null, "test");
            calls = module.Calls;
            Assert.Throws<InvalidOperationException>(() => versions.Commit(revision, null));
            Assert.Equal(calls, module.Calls); // Nor inside config.db COMMIT.
            Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
            versions.Abandon(revision, "expected block");
        }
        var pending = failed ? queue.ReadFailed(1, 0, 10) : queue.ReadPending(1, 0, 10);
        Assert.Equal("{\"exact\":9007199254740993}", System.Text.Encoding.UTF8.GetString(Assert.Single(pending).Payload.Span));
        Assert.Null(versions.TargetChangeProblem(Plan(OutputModuleCatalog.ReadProtocol(original)!))); // Module → same native recipient is not a target change.
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    public async Task MigrationAllowsOnlyConfirmedAddressChangeAndNotHiddenPathChange(string protocol)
    {
        using var data = TemporaryDirectory.Create();
        var module = new TestProtocolOutputModule(protocol);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services.AddSingleton<IOutputModuleFactory>(module));
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(module.Unit(Connection(protocol, "old"))), RuntimeSnapshotOrigins.Seed, null, "test"), null);
        var source = protocol == "http" ? OutputRecipientIdentity.Http("http://old.test/base/", null) : OutputRecipientIdentity.Mqtt("mqtt://old.test:1883", false, null);
        var target = protocol == "http" ? OutputRecipientIdentity.Http("http://new.test/base/", null) : OutputRecipientIdentity.Mqtt("mqtt://new.test:1883", false, null);
        var intent = new DeliveryMigrationIntentDraft(new(host.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId, Guid.NewGuid(), 1, "old"),
            source, target.Address, true, new("new", target));
        await host.Services.GetRequiredService<DeliveryCache>().EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), "old", source.Freeze(), "send")], Token);
        Assert.Null(versions.TargetChangeProblem(Plan(module.Unit(Connection(protocol, "new"))), [intent]));
        Assert.Throws<InvalidOperationException>(() => versions.TargetChangeProblem(Plan(module.Unit(Connection(protocol, "new"), "changed")), [intent]));
        Assert.Throws<InvalidOperationException>(() => versions.TargetChangeProblem(Plan(), [intent]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignCatalogAndStoredReaderCannotBypassFreezeAdmission(bool draft)
    {
        await using var host = new TenonEdgeApplicationFactory();
        var module = new TestProtocolOutputModule("http");
        var plan = Plan(module.Unit(Connection("http", "old")));
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        if (draft)
        { Assert.Throws<RuntimePlanDocumentException>(() => versions.FreezeDraft(plan, "0", "test")); }
        else
        { Assert.Throws<RuntimePlanDocumentException>(() => versions.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "test")); }
        Assert.Null(versions.Load(1));
        Assert.Null(versions.ReadPointers().PendingRevision);
    }
}

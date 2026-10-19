using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ExtensionOutputProtectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "recipient", false)]
    [InlineData(false, "address", false)]
    [InlineData(false, "delete", false)]
    [InlineData(true, "recipient", false)]
    [InlineData(true, "address", false)]
    [InlineData(true, "delete", false)]
    [InlineData(false, "recipient", true)]
    [InlineData(false, "address", true)]
    [InlineData(false, "delete", true)]
    [InlineData(true, "recipient", true)]
    [InlineData(true, "address", true)]
    [InlineData(true, "delete", true)]
    public async Task NewTransportCannotBypassFrozenRecipientAndPathProtection(bool failed, string change, bool afterFreeze)
    {
        var module = new TestWireOutputModule();
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton(TestWireOutputModule.Definition);
            services.AddSingleton<IOutputModuleFactory>(module);
        });
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var original = RuntimePlanDocument.ParseStored(TestProtocolOutputModule.Document(module.Unit()));
        versions.Commit(versions.Freeze(original, RuntimeSnapshotOrigins.Seed, null, "test"), null);
        var changed = RuntimePlanDocument.ParseStored(TestProtocolOutputModule.Document(change switch
        {
            "recipient" => [module.Unit(port: 19002)],
            "address" => [module.Unit(address: "other/{deviceKey}")],
            _ => [],
        }));
        Assert.Null(versions.TargetChangeProblem(changed));
        var frozen = afterFreeze ? versions.Freeze(changed, RuntimeSnapshotOrigins.Seed, null, "test") : 0;
        var queue = host.Services.GetRequiredService<DeliveryCache>();
        var sequence = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{\"value\":9007199254740993}"u8.ToArray(), "original", "summary", "events/meter")], Token)).Seqs);
        if (failed)
        { await queue.RecordFailureAsync(new(sequence, 1, "rejected", true, false), Token); }
        var calls = module.Calls;
        module.Throw = true;
        Assert.NotNull(versions.TargetChangeProblem(changed));
        if (afterFreeze)
        { Assert.Throws<InvalidOperationException>(() => versions.Commit(frozen, null)); }
        Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(RuntimeSnapshotStates.Effective, versions.Load(1)!.State);
        Assert.Single(failed ? queue.ReadFailed(1, 0, 10) : queue.ReadPending(1, 0, 10));
        Assert.Equal(calls, module.Calls); // Guard uses frozen values, never extension code inside a transaction.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewTransportAddressMigrationStillRejectsDeletionAndHiddenRouteChange(bool failed)
    {
        var module = new TestWireOutputModule();
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton(TestWireOutputModule.Definition);
            services.AddSingleton<IOutputModuleFactory>(module);
        });
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        RuntimePlanDocument Plan(int port, string address = "events/{deviceKey}") =>
            RuntimePlanDocument.ParseStored(TestProtocolOutputModule.Document(module.Unit(port, address)));
        versions.Commit(versions.Freeze(Plan(19001), RuntimeSnapshotOrigins.Seed, null, "test"), null);
        var source = OutputRecipientIdentity.Extension("wire", "wire://127.0.0.1:19001", "none", null);
        var target = OutputRecipientIdentity.Extension("wire", "wire://127.0.0.1:19002", "none", null);
        var intent = new DeliveryMigrationIntentDraft(new(host.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            Guid.NewGuid(), 1, "old"), source, target.Address, true, new("new", target));
        var queue = host.Services.GetRequiredService<DeliveryCache>();
        var sequence = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), "old", source.Freeze(), "events/meter")], Token)).Seqs);
        if (failed)
        { await queue.RecordFailureAsync(new(sequence, 1, "rejected", true, false), Token); }
        Assert.Null(versions.TargetChangeProblem(Plan(19002), [intent]));
        Assert.Throws<InvalidOperationException>(() => versions.TargetChangeProblem(Plan(19002, "other/{deviceKey}"), [intent]));
        Assert.Throws<InvalidOperationException>(() => versions.TargetChangeProblem(RuntimePlanDocument.ParseStored(TestProtocolOutputModule.Document()), [intent]));
    }
}

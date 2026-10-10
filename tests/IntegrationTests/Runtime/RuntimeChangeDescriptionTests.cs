using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeChangeDescriptionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApplyAndRollbackRecordTheirOwnDescriptionsWithoutChangingConfigurationContent()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var first = await coordinator.ApplyDraftAsync("0", "operator", null, Token, "  First change\n第二行  ");
        var second = await coordinator.ApplyDraftAsync("0", "operator", null, Token, "Second change");
        var rollback = await coordinator.RollbackAsync(first.Revision, "0", "operator", null, Token, "Return to first");
        Assert.True(rollback.Activated);
        Assert.Equal("First change\n第二行", versions.Load(first.Revision)!.ChangeDescription);
        Assert.Equal("Second change", versions.Load(second.Revision)!.ChangeDescription);
        Assert.Equal("Return to first", versions.Load(rollback.Revision)!.ChangeDescription);
        Assert.Equal(versions.Load(first.Revision)!.ContentHash, versions.Load(second.Revision)!.ContentHash);
        Assert.Equal(versions.Load(first.Revision)!.Content, versions.Load(rollback.Revision)!.Content);
        Assert.Equal(["Return to first", "Second change", "First change\n第二行"],
            versions.ReadHistory().Select(row => row.ChangeDescription));
        Assert.DoesNotContain("Return to first", System.Text.Json.JsonSerializer.Serialize(coordinator.ReadApplications()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrozenDescriptionSurvivesAbandonmentAndAutomaticRollbackDoesNotCopyIt()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        await coordinator.ApplyDraftAsync("0", "operator", null, Token, "Manual explanation");
        var automatic = versions.FreezeRollback(versions.Load(1)!, RuntimeVersionStore.SystemOperator);
        Assert.Null(versions.Load(automatic)!.ChangeDescription);
        versions.Abandon(automatic, "fixture");
        var failed = versions.FreezeDraft(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8), "0", "operator", "Not activated");
        versions.Abandon(failed, "fixture");
        Assert.Equal("Not activated", versions.ReadHistory()[0].ChangeDescription);
        Assert.Equal("Manual explanation", versions.Load(1)!.ChangeDescription);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task EmptyDescriptionsAreOptionalAndStoredAsNull(string? description)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync("0", "operator", null, Token, description);
        Assert.Null(factory.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ChangeDescription);
    }

    [Fact]
    public async Task DescriptionLimitIsExactAndSqlMetacharactersRemainData()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var text = "'; DROP TABLE runtime_snapshot; --\n".PadRight(1000, '字');
        await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync("0", "operator", null, Token, text);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(text, versions.Load(1)!.ChangeDescription);
        Assert.Single(versions.ReadHistory());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidDescriptionsAreRejectedBeforeAdmissionOrRevisionAllocation(bool tooLong)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var text = tooLong ? new string('x', 1001) : "invalid\0description";
        await Assert.ThrowsAsync<ArgumentException>(async () => await coordinator.ApplyDraftAsync("0", "operator", null, Token, text));
        await Assert.ThrowsAsync<ArgumentException>(async () => await coordinator.RollbackAsync(1, "0", "operator", null, Token, text));
        Assert.Throws<ArgumentException>(() => versions.FreezeDraft(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8), "0", "operator", text));
        Assert.Empty(coordinator.ReadApplications());
        Assert.Empty(versions.ReadHistory());
        Assert.Null(versions.ReadPointers().PendingRevision);
    }

    [Fact]
    public async Task DescriptionRemainsInHistoryAfterHostRestart()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync("0", "operator", null, Token, "Persistent note");
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        var history = restarted.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory();
        Assert.Equal("Persistent note", Assert.Single(history).ChangeDescription);
    }

    [Fact]
    public async Task FreezeFailureRollsBackDescriptionSnapshotAndRevisionAllocationTogether()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER reject_freeze BEFORE UPDATE OF pending_revision ON system_state BEGIN SELECT RAISE(ABORT, 'fixture'); END");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ApplyDraftAsync("0", "operator", null, Token, "Rejected note"));
            Assert.Empty(versions.ReadHistory());
            Assert.Null(versions.ReadPointers().PendingRevision);
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER reject_freeze"); }
        var applied = await coordinator.ApplyDraftAsync("0", "operator", null, Token, "Retry note");
        Assert.Equal(1, applied.Revision);
        Assert.Equal("Retry note", Assert.Single(versions.ReadHistory()).ChangeDescription);
    }

    [Fact]
    public void UpgradeFromSchema15PreservesExistingVersionsAndAddsNullableDescriptionOnlyOnce()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 15).ToArray());
        store.Database.Client.Ado.ExecuteCommand("""
            INSERT INTO runtime_snapshot (revision, schema_version, content, content_hash, origin, state, created_at, created_by)
            VALUES (7, 1, 'fixture-content', 'fixture-hash', 'seed', 'effective', 123, 'system')
            """);
        store.CreateMigrator().Migrate();
        var record = store.Database.Client.Queryable<RuntimeSnapshotRecord>().InSingle(7);
        Assert.Equal("fixture-content", record.Content);
        Assert.Equal("fixture-hash", record.ContentHash);
        Assert.Null(record.ChangeDescription);
        record.ChangeDescription = "Added after upgrade";
        store.Database.Client.Updateable(record).UpdateColumns(row => row.ChangeDescription).ExecuteCommand();
        store.CreateMigrator().Migrate();
        Assert.Equal("Added after upgrade", store.Database.Client.Queryable<RuntimeSnapshotRecord>().InSingle(7).ChangeDescription);
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM schema_migration WHERE version = 16"));
    }
}

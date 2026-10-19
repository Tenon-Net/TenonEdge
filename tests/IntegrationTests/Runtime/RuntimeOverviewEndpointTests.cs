using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeOverviewEndpointTests
{
    private const string Path = "/api/edge-admin/v1/runtime/status";
    private static RuntimePlanDocument Document() => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Configurator")]
    [InlineData("Viewer")]
    public async Task AuthenticatedOverviewPairsDurableVersionWithItsApplicationMetadataNotTheActiveRevision(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var client = await factory.CreateLoggedInClientAsync("reader", TestContext.Current.CancellationToken);
        var empty = await client.GetJsonAsync(Path, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("configuration").GetProperty("effectiveRevision").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("configuration").GetProperty("appliedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("configuration").GetProperty("appliedAt").ValueKind);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var revision = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "actual-applicant");
        versions.Commit(revision, null);
        var newer = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "not-applied");
        versions.Abandon(newer, "fixture-prepare-failed");
        using var response = await client.GetAsync(new Uri(Path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var status = document.RootElement;
        var configuration = status.GetProperty("configuration");
        Assert.Equal(revision, configuration.GetProperty("effectiveRevision").GetInt64());
        Assert.Equal("actual-applicant", configuration.GetProperty("appliedBy").GetString());
        Assert.Equal(versions.Load(revision)!.ActivatedAt, configuration.GetProperty("appliedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        // Store commits alone do not start a runtime: this is the durable-vs-active boundary, not a successful runtime application claim.
        Assert.Equal(JsonValueKind.Null, status.GetProperty("runtime").GetProperty("activeRevision").ValueKind);
        Assert.DoesNotContain("not-applied", configuration.GetRawText());
        Assert.DoesNotContain("content", configuration.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetadataReadFailureIsUnknownAndDoesNotHideOtherRuntimeDiagnostics()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("reader", TestContext.Current.CancellationToken);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("ALTER TABLE runtime_snapshot RENAME TO unavailable_snapshot_fixture");
        try
        {
            var status = await client.GetJsonAsync(Path, TestContext.Current.CancellationToken);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("configuration").ValueKind);
            Assert.Equal(JsonValueKind.Object, status.GetProperty("runtime").ValueKind);
            Assert.DoesNotContain("unavailable_snapshot_fixture", status.GetRawText());
            Assert.DoesNotContain("no such table", status.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally { database.Client.Ado.ExecuteCommand("ALTER TABLE unavailable_snapshot_fixture RENAME TO runtime_snapshot"); }
        var recovered = await client.GetJsonAsync(Path, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, recovered.GetProperty("configuration").ValueKind);
        Assert.Equal(JsonValueKind.Null, recovered.GetProperty("configuration").GetProperty("effectiveRevision").ValueKind);
    }

    [Fact]
    public async Task SummaryUsesOneMetadataOnlySelectAndReadsCommittedStateWithoutWaitingForWriter()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var old = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "old-operator");
        versions.Commit(old, null);
        var expected = versions.ReadEffectiveConfiguration();
        var next = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "pending-operator");
        using var writer = database.CreateIsolatedClient();
        writer.Ado.BeginTran();
        try
        {
            // A separate real SQLite transaction stages a partially written candidate. The read must see the old committed pointer AND metadata.
            writer.Ado.ExecuteCommand("UPDATE system_state SET effective_revision = @revision WHERE id = 1", new SqlSugar.SugarParameter("@revision", next));
            writer.Ado.ExecuteCommand("UPDATE runtime_snapshot SET created_by = 'uncommitted-operator', activated_at = 1 WHERE revision = @revision", new SqlSugar.SugarParameter("@revision", old));
            var result = await Task.Run(() =>
            {
                var queries = new List<string>();
                var client = database.Client;
                var original = client.CurrentConnectionConfig.AopEvents.OnLogExecuting;
                client.Aop.OnLogExecuting = (sql, _) => queries.Add(sql);
                try
                {
                    var actual = versions.ReadEffectiveConfiguration();
                    var select = Assert.Single(queries);
                    Assert.Contains("SELECT", select, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("LEFT JOIN", select, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("content", select, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("*", select, StringComparison.Ordinal);
                    return actual;
                }
                finally { client.Aop.OnLogExecuting = original; }
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(expected, result);
        }
        finally { writer.Ado.RollbackTran(); }
        versions.Commit(next, null);
        Assert.Equal(new RuntimeEffectiveConfiguration(next, "pending-operator",
            DateTimeOffset.FromUnixTimeMilliseconds(versions.Load(next)!.ActivatedAt!.Value)), versions.ReadEffectiveConfiguration());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenSnapshotCannotTurnEffectivePointerIntoEmptyConfiguration(bool missing)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var revision = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "known-operator");
        versions.Commit(revision, null);
        var expected = versions.ReadEffectiveConfiguration();
        database.Client.Ado.ExecuteCommand(missing ? "DELETE FROM runtime_snapshot" : "UPDATE runtime_snapshot SET content = 'invalid-private-fixture-content'");
        var actual = versions.ReadEffectiveConfiguration();
        Assert.Equal(revision, actual.EffectiveRevision);
        if (missing)
        {
            Assert.Null(actual.AppliedBy);
            Assert.Null(actual.AppliedAt);
        }
        else
        { Assert.Equal(expected, actual); }
    }

    [Fact]
    public async Task SummaryUsesCommitTimeNotFreezeTimeAndDoesNotInventInvalidTime()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var revision = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, RuntimeVersionStore.SystemOperator);
        database.Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET created_at = 1");
        versions.Commit(revision, null);
        var actual = versions.ReadEffectiveConfiguration();
        Assert.Equal(RuntimeVersionStore.SystemOperator, actual.AppliedBy);
        Assert.Equal(versions.Load(revision)!.ActivatedAt, actual.AppliedAt!.Value.ToUnixTimeMilliseconds());
        Assert.NotEqual(1, actual.AppliedAt.Value.ToUnixTimeMilliseconds());
        database.Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET activated_at = 9223372036854775807");
        Assert.Null(versions.ReadEffectiveConfiguration().AppliedAt);
    }
}

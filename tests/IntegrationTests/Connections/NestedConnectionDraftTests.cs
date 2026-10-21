using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class NestedConnectionDraftTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ConnectionModuleDefinition Definition => new("wrapper", 1, "Wrapper", new("wrapper", ParameterScope.Connection,
        [new("label", "Label", ParameterType.Text) { Required = true, MaximumLength = 64 },
         new("upstream", "Upstream", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http", "wrapper"] }, new("auth", "Auth", ParameterType.Credential)]), ["label"]);
    private static FixedDataDirectoryApplicationFactory Host(string path) => new(path,
        configureServices: services => services.AddSingleton(Definition));

    [Fact]
    public async Task NestedHttpFixturePreparesWithTheOriginalHttpFactoryWithoutNetworkOrCredentials()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services
            .AddSingleton(TestNestedHttpModule.Connection)
            .AddSingleton<IInputModuleFactory>(provider => new TestNestedHttpModule(provider.GetServices<IInputUnitFactory>().OfType<HttpInputFactory>().Single())));
        var store = host.Services.GetRequiredService<ConnectionDraftStore>();
        var leaf = store.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var parent = store.Write(null, new(leaf.Version, "parent", "Parent", ConnectionKind.Module,
            Module: new("nestedhttp", 1, JsonSerializer.SerializeToElement(new { label = "local" }), new() { ["upstream"] = leaf.Connection.Id })), "fixture", null);
        var frozen = parent.Connection.Module!.Snapshot(parent.Connection.Id, store.Read().Connections.ToDictionary(value => value.Id));
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create("input.nestedhttp", "meter/source"), JsonSerializer.SerializeToElement(new
        {
            inputModule = new
            {
                version = 1,
                parameters = new { connection = parent.Connection.Id.ToString(CultureInfo.InvariantCulture) },
                connections = new { connection = frozen },
            },
        }, JsonSerializerOptions.Web));
        unit = host.Services.GetRequiredService<InputModuleCatalog>().WithFrozenFields(unit);
        await using var prepared = host.Services.GetRequiredService<RuntimeHost>().Prepare(new(1, [unit]));
        Assert.Empty(prepared.Failures);
    }

    [Theory]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    [InlineData("Viewer")]
    public async Task ExplicitConnectionReferenceIsSavedAndBlocksDeletingItsDependency(string role)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var store = host.Services.GetRequiredService<ConnectionDraftStore>();
        var leaf = store.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        using var saved = await client.PostJsonAsync(Endpoint, new
        {
            expectedVersion = leaf.Version,
            key = "wrapper",
            name = "Wrapper",
            kind = "module",
            module = new { kind = "wrapper", version = 1, parameters = new { label = "local" }, connections = new { upstream = leaf.Connection.Id } }
        }, Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, saved.StatusCode);
        if (role == "Viewer")
        {
            Assert.Equal(leaf.Version, store.Read().Version);
            Assert.Single(store.Read().Connections);
            return;
        }
        var result = await saved.ReadJsonAsync(Token);
        var parentId = result.GetProperty("connection").GetProperty("id").GetInt64();
        Assert.Equal(leaf.Connection.Id, result.GetProperty("connection").GetProperty("module").GetProperty("connections").GetProperty("upstream").GetInt64());
        var version = result.GetProperty("version").GetString()!;
        var review = await client.GetJsonAsync($"{Endpoint}/{leaf.Connection.Id}/deletion-review?expectedVersion={version}", Token);
        Assert.False(review.GetProperty("canDelete").GetBoolean());
        Assert.Equal(parentId, Assert.Single(review.GetProperty("connections").EnumerateArray()).GetProperty("id").GetInt64());
        using var deleted = await client.DeleteAsync($"{Endpoint}/{leaf.Connection.Id}?expectedVersion={version}", Token);
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal(version, store.Read().Version);
        Assert.Equal(2, store.Read().Connections.Length);
    }
    private static ConnectionDraftResult Parent(ConnectionDraftStore store, string version, long child, string key = "wrapper") =>
        store.Write(null, new(version, key, key, ConnectionKind.Module,
            Module: new("wrapper", 1, JsonSerializer.SerializeToElement(new { label = "local" }), new() { ["upstream"] = child })), "fixture", null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossGatewayImportResolvesForwardBusinessKeysAndPersistsWithoutApplying(bool replace)
    {
        using var sourceData = TemporaryDirectory.Create();
        using var targetData = TemporaryDirectory.Create();
        byte[] exported;
        long sourceId;
        await using (var source = Host(sourceData.Path))
        {
            var drafts = source.Services.GetRequiredService<ConnectionDraftStore>();
            var leaf = drafts.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
            _ = Parent(drafts, leaf.Version, leaf.Connection.Id);
            sourceId = leaf.Connection.Id;
            exported = source.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token);
        }
        Assert.DoesNotContain(sourceId.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetString(exported), StringComparison.Ordinal);
        var file = ConfigurationImportDocument.Read(exported, Token);
        Assert.Equal("leaf", Assert.Single(file.Connections, value => value.Module is not null).Module!.Connections!["upstream"]);
        file = file with { Connections = file.Connections.OrderByDescending(value => value.Module is not null).ToArray() };
        var bytes = ConfigurationExport.Serialize(file, Token);
        long localId;
        string version;
        await using (var target = Host(targetData.Path))
        {
            var drafts = target.Services.GetRequiredService<ConnectionDraftStore>();
            var local = drafts.Write(null, new("0", "leaf", "Existing", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
            localId = local.Connection.Id;
            Assert.NotEqual(sourceId, localId);
            var store = target.Services.GetRequiredService<ConfigurationImportStore>();
            var mode = replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge;
            var candidate = store.Prepare(bytes, mode, local.Version, Token);
            Assert.Equal(localId, Assert.Single(candidate.Draft.Connections, value => value.Module is not null).Module!.Connections!["upstream"]);
            TestAccounts.Create(target.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
            var actor = TestAccounts.Find(target.Services, "operator")!;
            version = store.Commit(candidate, new(actor.Id, actor.SecurityStamp, null), Token).Version;
            Assert.Empty(store.Prepare(bytes, mode, version, Token).Changes);
            Assert.Null(target.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
            var badParent = file.Connections[0] with { Module = file.Connections[0].Module! with { Connections = new() { ["upstream"] = "absent" } } };
            var bad = file with { Connections = [badParent, file.Connections[1]] };
            Assert.Throws<ConfigurationImportException>(() => store.Prepare(ConfigurationExport.Serialize(bad, Token), mode, version, Token));
            Assert.Equal(version, drafts.Read().Version);
        }
        await using var restarted = Host(targetData.Path);
        var restored = restarted.Services.GetRequiredService<ConnectionDraftStore>().Read();
        Assert.Equal(version, restored.Version);
        Assert.Equal(localId, Assert.Single(restored.Connections, value => value.Module is not null).Module!.Connections!["upstream"]);
        Assert.Null(restarted.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }

    [Fact]
    public async Task CyclesStaleVersionsAndAuditFailuresLeaveTheOriginalReferencesIntact()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var leaf = drafts.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var first = Parent(drafts, leaf.Version, leaf.Connection.Id, "first");
        var second = Parent(drafts, first.Version, first.Connection.Id, "second");
        var update = new ConnectionDraftRequest(second.Version, "first", "First", ConnectionKind.Module,
            Module: first.Connection.Module! with { Connections = new() { ["upstream"] = second.Connection.Id } });
        Assert.Throws<ArgumentException>(() => drafts.Write(first.Connection.Id, update, "fixture", null));
        Assert.Equal(second.Version, drafts.Read().Version);
        Assert.Throws<DraftWriteException>(() => drafts.Delete(leaf.Connection.Id, first.Version, "fixture", null));
        host.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            "CREATE TRIGGER fail_nested_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'connection.draftUpdated' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        var failure = Assert.ThrowsAny<Exception>(() => drafts.Write(second.Connection.Id, update with { Key = "second", Module = first.Connection.Module }, "fixture", null));
        Assert.Contains("fixture", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(second.Version, drafts.Read().Version);
        Assert.Equal(first.Connection.Id, drafts.Read().Connections.Single(value => value.Id == second.Connection.Id).Module!.Connections!["upstream"]);
    }

    [Fact]
    public async Task DisabledDescendantBlocksCompilationButNotReadingOrExportingDrafts()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var leaf = drafts.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, false, Http: new("http://localhost/")), "fixture", null);
        var parent = Parent(drafts, leaf.Version, leaf.Connection.Id);
        Assert.NotEmpty(host.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(
            host.Services.GetRequiredService<RouteDraftStore>().ReadCompilation(parent.Version, Token), Token));
        Assert.Equal(parent.Version, drafts.Read().Version);
    }

    [Fact]
    public async Task CredentialReviewUsesCompleteDraftButKeepsTheExplicitScalarRecipientBinding()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var leaf = drafts.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var parent = Parent(drafts, leaf.Version, leaf.Connection.Id);
        var credentials = host.Services.GetRequiredService<ModuleCredentialStore>();
        var metadata = await credentials.WriteAsync(parent.Connection.Id, new(parent.Version,
            Secrets: new() { ["auth"] = "fixture-secret" }, Account: "test-account"), "fixture", null, false, Token);
        Assert.True(metadata.MatchesDraft);
        var review = credentials.Review(parent.Connection.Id, new(parent.Version, metadata.Identity.Version, "test-account"), false);
        Assert.Equal("local", review.RecipientParameters.GetProperty("label").GetString());
        var changed = drafts.Write(leaf.Connection.Id, new(parent.Version, "leaf", "Leaf", ConnectionKind.Http, Http: new("http://changed/")), "fixture", null);
        Assert.True(credentials.Read(parent.Connection.Id)!.MatchesDraft);
        var changedParent = drafts.Write(parent.Connection.Id, new(changed.Version, "wrapper", "Wrapper", ConnectionKind.Module,
            Module: parent.Connection.Module! with { Parameters = JsonSerializer.SerializeToElement(new { label = "other" }) }), "fixture", null);
        Assert.False(credentials.Read(parent.Connection.Id)!.MatchesDraft);
        var exported = Encoding.UTF8.GetString(host.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        Assert.DoesNotContain("fixture-secret", exported, StringComparison.Ordinal);
        Assert.DoesNotContain(leaf.Connection.Id.ToString(CultureInfo.InvariantCulture), exported, StringComparison.Ordinal);
        Assert.Equal(changedParent.Version, drafts.Read().Version);
    }

    [Fact]
    public async Task UninstalledDeclarationStillAllowsReadExportAndReferenceProtectedDeletion()
    {
        using var data = TemporaryDirectory.Create();
        long leafId;
        long parentId;
        string version;
        await using (var host = Host(data.Path))
        {
            var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
            var leaf = drafts.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
            var parent = Parent(drafts, leaf.Version, leaf.Connection.Id);
            (leafId, parentId, version) = (leaf.Connection.Id, parent.Connection.Id, parent.Version);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        var store = restarted.Services.GetRequiredService<ConnectionDraftStore>();
        Assert.Equal(2, store.Read().Connections.Length);
        var exported = ConfigurationImportDocument.Read(restarted.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token), Token);
        Assert.Equal("leaf", Assert.Single(exported.Connections, item => item.Module is not null).Module!.Connections!["upstream"]);
        var review = store.ReviewDeletion(leafId, version);
        Assert.False(review.CanDelete);
        Assert.Equal(parentId, Assert.Single(review.Connections).Id);
        Assert.Equal("connection.connectionReferenced", Assert.Throws<DraftWriteException>(() => store.Delete(leafId, version, "fixture", null)).Code);
        var deleted = store.Delete(parentId, version, "fixture", null);
        _ = store.Delete(leafId, deleted.Version, "fixture", null);
        Assert.Empty(store.Read().Connections);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("scalar")]
    public async Task InvalidReferenceEditsAreRejectedByTheRealApiWithoutChangingTheDraft(string invalid)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var store = host.Services.GetRequiredService<ConnectionDraftStore>();
        var leaf = store.Write(null, new("0", "leaf", "Leaf", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var parent = Parent(store, leaf.Version, leaf.Connection.Id);
        var references = new Dictionary<string, long> { [invalid == "unknown" ? "unexpected" : "upstream"] = invalid == "self" ? parent.Connection.Id : invalid == "missing" ? 1 : leaf.Connection.Id };
        var parameters = new Dictionary<string, object> { ["label"] = "local" };
        if (invalid == "scalar")
        { parameters["upstream"] = leaf.Connection.Id; }
        using var response = await client.PutAsJsonAsync($"{Endpoint}/{parent.Connection.Id}", new
        {
            expectedVersion = parent.Version,
            key = "wrapper",
            name = "Wrapper",
            kind = "module",
            module = new { kind = "wrapper", version = 1, parameters, connections = references },
        }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(parent.Version, store.Read().Version);
        Assert.Equal(leaf.Connection.Id, store.Read().Connections.Single(item => item.Id == parent.Connection.Id).Module!.Connections!["upstream"]);
    }

}

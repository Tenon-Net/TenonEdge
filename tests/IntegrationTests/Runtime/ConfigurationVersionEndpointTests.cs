using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationVersionEndpointTests
{
    private const string Root = "/api/edge-admin/v1/configuration/versions";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimePlanDocument Plan() => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task AllRolesCanInspectFrozenContentsAndCompareInEitherDirectionWithoutWriting(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Plan(), "0", "first"), null);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "meter", "Frozen", DeviceKind.Simulator, Enabled: false), "editor", null);
        versions.Commit(versions.FreezeDraft(Plan(), saved.Version, "second", "Added disabled device"), null);
        var edited = devices.Write(saved.Device.Id, new(saved.Version, "meter", "Later draft", DeviceKind.Simulator, Enabled: false), "later-editor", null);
        var before = versions.ReadPointers();
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); // Read-only GETs do not require CSRF.
        using var response = await client.GetAsync(Root + "/2", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var detail = await response.ReadJsonAsync(Token);
        Assert.Equal("Added disabled device", detail.GetProperty("version").GetProperty("changeDescription").GetString());
        Assert.Equal("Frozen", detail.GetProperty("draft").GetProperty("devices")[0].GetProperty("name").GetString());
        Assert.Empty(detail.GetProperty("plan").GetProperty("units").EnumerateArray());
        Assert.Equal(versions.Load(2)!.ContentHash, detail.GetProperty("contentHash").GetString());
        Assert.Equal(Plan().ContentHash, detail.GetProperty("planHash").GetString());
        var forward = await client.GetJsonAsync(Root + "/1/compare/2", Token);
        var change = Assert.Single(forward.GetProperty("changes").EnumerateArray());
        Assert.Equal("added", change.GetProperty("change").GetString());
        Assert.Equal("editor", change.GetProperty("updatedBy").GetString());
        Assert.Equal(1, forward.GetProperty("beforeRevision").GetInt64());
        Assert.Equal(2, forward.GetProperty("afterRevision").GetInt64());
        var reverse = await client.GetJsonAsync(Root + "/2/compare/1", Token);
        Assert.Equal("removed", Assert.Single(reverse.GetProperty("changes").EnumerateArray()).GetProperty("change").GetString());
        var draft = await client.GetJsonAsync(Root + "/2/compare-draft?expectedVersion=" + edited.Version, Token);
        Assert.Equal(edited.Version, draft.GetProperty("draftVersion").GetString());
        Assert.Equal("Later draft", Assert.Single(draft.GetProperty("changes").EnumerateArray()).GetProperty("after").GetProperty("name").GetString());
        var same = await client.GetJsonAsync(Root + "/2/compare/2", Token);
        Assert.Empty(same.GetProperty("changes").EnumerateArray());
        Assert.Equal(before, versions.ReadPointers());
        Assert.Equal(2, versions.ReadHistory().Length);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
        Assert.Equal(edited.Version, devices.Read().Version);
    }

    [Fact]
    public async Task ComparisonSummarizesModbusPointChangesByDevice()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "modbus", "Modbus", ConnectionKind.Modbus, Modbus: new("192.168.1.10")), "editor", null);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Modbus, ConnectionId: connection.Connection.Id, UnitId: 1), "editor", null);
        var a = new ModbusPointSettings { Key = "a", FunctionCode = 3, Address = 0, Type = "uint16" };
        var b = a with { Key = "b", Address = 1 };
        var inputs = factory.Services.GetRequiredService<InputDraftStore>();
        var first = inputs.Write(device.Device.Id, null, new(device.Version, "group", "Group", Modbus: new(1000, [a, b])), "editor", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Plan(), first.Version, "editor"), null);
        var second = inputs.Write(device.Device.Id, first.Input.Id,
            new(first.Version, "group", "Group", Modbus: new(1000, [a with { Address = 2 }, b with { Key = "c" }])), "editor", null);
        versions.Commit(versions.FreezeDraft(Plan(), second.Version, "editor"), null);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        foreach (var suffix in new[] { "/1/compare/2", "/1/compare-draft?expectedVersion=" + second.Version })
        {
            var result = await client.GetJsonAsync(Root + suffix, Token);
            var points = Assert.Single(result.GetProperty("points").EnumerateArray());
            Assert.Equal(device.Device.Id, points.GetProperty("deviceId").GetInt64());
            Assert.Equal(1, points.GetProperty("added").GetInt32());
            Assert.Equal(1, points.GetProperty("modified").GetInt32());
            Assert.Equal(1, points.GetProperty("removed").GetInt32());
        }
    }

    [Fact]
    public async Task AnonymousCannotReadAnyVersionEndpoint()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        foreach (var suffix in new[] { "/", "/1", "/1/compare/2", "/1/compare-draft?expectedVersion=0" })
        {
            using var response = await client.GetAsync(Root + suffix, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, Token);
        }
    }

    [Fact]
    public async Task MetadataPaginationAndFailureDescriptionsSurviveHostRestart()
    {
        using var data = TemporaryDirectory.Create();
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            for (var i = 0; i < 3; i++)
            { versions.Commit(versions.FreezeDraft(Plan(), "0", "editor", "version " + (i + 1)), null); }
            versions.Abandon(versions.FreezeDraft(Plan(), "0", "editor", "failed change"), "fixture-rejected");
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await restarted.CreateLoggedInClientAsync("reader", Token);
        using var response = await client.GetAsync(Root + "/?limit=2", Token);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = await response.ReadJsonAsync(Token);
        Assert.Equal(new long[] { 4, 3 }, page.EnumerateArray().Select(item => item.GetProperty("revision").GetInt64()));
        Assert.Equal("notEffective", page[0].GetProperty("state").GetString());
        Assert.Equal("fixture-rejected", page[0].GetProperty("notEffectiveReason").GetString());
        Assert.False(page[0].TryGetProperty("content", out _));
        var next = await client.GetJsonAsync(Root + "/?limit=2&beforeRevision=3", Token);
        Assert.Equal(new long[] { 2, 1 }, next.EnumerateArray().Select(item => item.GetProperty("revision").GetInt64()));
        var empty = await client.GetJsonAsync(Root + "/?beforeRevision=1", Token);
        Assert.Empty(empty.EnumerateArray());
        var failed = await client.GetJsonAsync(Root + "/4", Token);
        Assert.Equal("failed change", failed.GetProperty("version").GetProperty("changeDescription").GetString());
        Assert.Empty(restarted.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task InvalidParametersAndStaleDraftAreRejectedWithoutConsumingTheBudget()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Plan(), "0", "editor"), null);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        foreach (var suffix in new[] { "/?limit=0", "/?limit=101", "/?beforeRevision=0", "/?beforeRevision=-1", "/0", "/-1", "/1/compare/0", "/0/compare/1", "/1/compare-draft", "/1/compare-draft?expectedVersion=%20" })
        {
            using var response = await client.GetAsync(Root + suffix, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.BadRequest, Token);
        }
        using var stale = await client.GetAsync(Root + "/1/compare-draft?expectedVersion=stale", Token);
        Assert.Contains("configuration.draftConflict", await ProblemAssert.HasStatusAsync(stale, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        using var valid = await client.GetAsync(Root + "/1", Token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Single(versions.ReadHistory());
    }

    [Fact]
    public async Task MissingAndPrunedVersionsReturnNotFoundAndListDoesNotParseSnapshotContents()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        for (var i = 0; i < 21; i++)
        { versions.Commit(versions.FreezeDraft(Plan(), "0", "editor"), null); }
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        foreach (var suffix in new[] { "/1", "/999", "/21/compare/1", "/1/compare/21", "/1/compare-draft?expectedVersion=0" })
        {
            using var response = await client.GetAsync(Root + suffix, Token);
            Assert.Contains("configuration.versionNotFound", await ProblemAssert.HasStatusAsync(response, HttpStatusCode.NotFound, Token), StringComparison.Ordinal);
        }
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET content = 'private-invalid-content' WHERE revision = 21");
        var list = await client.GetJsonAsync(Root + "/", Token);
        Assert.Equal(20, list.GetArrayLength());
        Assert.DoesNotContain("private-invalid-content", list.GetRawText(), StringComparison.Ordinal);
        foreach (var suffix in new[] { "/21", "/20/compare/21", "/21/compare-draft?expectedVersion=0" })
        {
            using var response = await client.GetAsync(Root + suffix, Token);
            var problem = await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token);
            Assert.Contains("configuration.invalidSnapshot", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("private-invalid-content", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LegacyPlanCanBeInspectedButMissingDraftIsNotInventedForComparison()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(), RuntimeSnapshotOrigins.Seed, Plan().ContentHash, "system"), null);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        var detail = await client.GetJsonAsync(Root + "/1", Token);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("draft").ValueKind);
        Assert.Equal("configuration.legacyComparisonUnavailable", detail.GetProperty("warnings")[0].GetString());
        Assert.Equal(1, detail.GetProperty("plan").GetProperty("schemaVersion").GetInt32());
        foreach (var suffix in new[] { "/1/compare/1", "/1/compare-draft?expectedVersion=0" })
        {
            using var response = await client.GetAsync(Root + suffix, Token);
            Assert.Contains("configuration.unsupportedSnapshot", await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CredentialSecretsAndCiphertextAreAbsentFromDetailAndComparisons()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Plan(), "0", "editor"), null);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "unused", "Unused", ConnectionKind.Http, Enabled: false, Http: new("http://localhost/")), "editor", null);
        factory.Services.GetRequiredService<HttpCredentialStore>().Write(connection.Connection.Id,
            new("http://localhost/", HttpCredentialKind.Bearer, Secret: "version-api-private-secret", ExpectedDraftVersion: connection.Version), "editor", null, true);
        var encrypted = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<HttpCredentialRecord>().Single().ProtectedSecret!;
        versions.Commit(versions.FreezeDraft(Plan(), connection.Version, "editor"), null);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        foreach (var suffix in new[] { "/", "/2", "/1/compare/2", "/1/compare-draft?expectedVersion=" + connection.Version })
        {
            using var response = await client.GetAsync(Root + suffix, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("version-api-private-secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain(encrypted, body, StringComparison.Ordinal);
            Assert.DoesNotContain("protectedSecret", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentAndComparisonShareBudgetButMetadataRemainsAvailable(bool global)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        var leases = Enumerable.Range(0, global ? 4 : 2).Select(i => budget.TryAcquire(global ? "other" + i : key)!).ToArray();
        try
        {
            foreach (var suffix in new[] { "/1", "/1/compare/2", "/1/compare-draft?expectedVersion=0" })
            {
                using var response = await client.GetAsync(Root + suffix, Token);
                await ProblemAssert.HasStatusAsync(response, HttpStatusCode.TooManyRequests, Token);
            }
            using var metadata = await client.GetAsync(Root + "/", Token);
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
    }
}

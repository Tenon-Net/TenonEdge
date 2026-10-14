using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeMessageEndpointTests
{
    private const string Root = "/api/edge-admin/v1/runtime/messages";
    private const long Revision = 9007199254740993;
    private static readonly RuntimeUnitId Source = RuntimeUnitId.Create("input.simulator", "meter/values");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimeUnitPlan Input(RuntimeUnitId id) => new(id, JsonSerializer.Deserialize<JsonElement>(
        """{"periodMs":86400000,"sample":{"v":9223372036854775807}}"""));
    private static string Query(RuntimeUnitId? id = null, long revision = Revision) =>
        $"kind={(id ?? Source).Kind}&key={Uri.EscapeDataString((id ?? Source).Key)}&revision={revision.ToString(CultureInfo.InvariantCulture)}";
    private static string Body(RuntimeMessageRecord record, long revision = Revision) =>
        $"{Root}/{record.Sequence.ToString(CultureInfo.InvariantCulture)}/body?{Query(record.ObjectId, revision)}&recordRevision={record.Revision.ToString(CultureInfo.InvariantCulture)}&sourceMessageId={record.SourceMessageId:D}";
    private static async Task<RuntimeHost> Start(TenonEdgeApplicationFactory factory, int count = 1)
    {
        var runtime = factory.Services.GetRequiredService<RuntimeHost>();
        var units = count == 1 ? new[] { Input(Source) } : Enumerable.Range(0, count)
            .Select(i => Input(RuntimeUnitId.Create("input.simulator", $"meter{i:D3}/values"))).ToArray();
        await runtime.ActivateAsync(runtime.Prepare(new(Revision, units)));
        await RuntimeTesting.ReadCapturedAsync(runtime, units[0].Id);
        return runtime;
    }

    [Fact]
    public async Task IdleAndQueueOnlyPlansExposeNoMessageObjects()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var runtime = factory.Services.GetRequiredService<RuntimeHost>();
        Assert.Null(runtime.GetMessageCatalog().Revision);
        Assert.Empty(runtime.GetMessageCatalog().Objects);
        var queue = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "queue/idle/sender"), JsonSerializer.Deserialize<JsonElement>(
            """{"connectionId":12,"baseAddress":"http://192.0.2.1/","queueOnly":true,"inputs":[]}"""));
        await runtime.ActivateAsync(runtime.Prepare(new(Revision, [queue])));
        Assert.Equal(Revision, runtime.GetMessageCatalog().Revision);
        Assert.Empty(runtime.GetMessageCatalog().Objects);
        Assert.Equal(0, runtime.Messages.Status.Records);
    }

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task AllRolesSeeMetadataButOnlyEditorsCanExplicitlyReadExactBody(string role, HttpStatusCode bodyStatus)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var runtime = await Start(factory);
        var record = Assert.Single(runtime.Messages.Read(Source, false));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var anonymous = factory.CreateClient();
        foreach (var path in new[] { Root + "/objects", Root + "?" + Query(), Body(record) })
        {
            using var denied = await anonymous.GetAsync(new Uri(path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var objects = await client.GetJsonAsync(Root + "/objects", Token);
        Assert.Equal("9007199254740993", objects.GetProperty("revision").GetString());
        Assert.Equal(Source.Key, Assert.Single(objects.GetProperty("items").EnumerateArray()).GetProperty("key").GetString());
        using var listing = await client.GetAsync(new Uri(Root + "?" + Query(), UriKind.Relative), Token);
        Assert.True(listing.Headers.CacheControl!.NoStore);
        var metadata = Assert.Single((await listing.ReadJsonAsync(Token)).GetProperty("items").EnumerateArray());
        Assert.Equal(record.Sequence.ToString(CultureInfo.InvariantCulture), metadata.GetProperty("sequence").GetString());
        Assert.Equal("received", metadata.GetProperty("details").GetProperty("outcome").GetString());
        Assert.False(metadata.TryGetProperty("body", out _));
        Assert.DoesNotContain("9223372036854775807", await listing.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        using var body = await client.GetAsync(new Uri(Body(record), UriKind.Relative), Token);
        Assert.Equal(bodyStatus, body.StatusCode);
        if (bodyStatus == HttpStatusCode.OK)
        {
            Assert.True(body.Headers.CacheControl!.NoStore);
            var response = await body.ReadJsonAsync(Token);
            Assert.Equal(runtime.Messages.Read(Source, true)[0].Body, response.GetProperty("body").GetString());
            Assert.Contains("9223372036854775807", response.GetProperty("body").GetString(), StringComparison.Ordinal);
        }
        Assert.Single(runtime.Messages.Read(Source, false)); // GET never samples
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task SelectionEnforcesPrivacyEvictionTruncationAndAllRecordIdentities()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var runtime = await Start(factory);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var old = runtime.Messages.Read(Source, false)[0];
        var now = DateTimeOffset.UtcNow;
        var secret = new EdgeMessage(Guid.NewGuid(), "values", "meter", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("fixture-private") }, diagnosticAccess: DiagnosticSampleAccess.Redacted);
        runtime.Messages.CaptureInput(Source, Revision, secret);
        var hidden = runtime.Messages.Read(Source, false)[0];
        using var denied = await client.GetAsync(new Uri(Body(hidden), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("fixture-private", await denied.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', 70000) + "\"}");
        runtime.Messages.Capture(Source, Revision, Guid.NewGuid(), bytes);
        var truncated = runtime.Messages.Read(Source, false)[0];
        var body = await client.GetJsonAsync(Body(truncated), Token);
        Assert.True(body.GetProperty("record").GetProperty("truncated").GetBoolean());
        Assert.Equal(bytes.Length, body.GetProperty("record").GetProperty("originalBytes").GetInt32());
        Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, Encoding.UTF8.GetByteCount(body.GetProperty("body").GetString()!));
        foreach (var wrong in new[] { truncated with { SourceMessageId = Guid.NewGuid() }, truncated with { Revision = Revision - 1 }, truncated with { Sequence = long.MaxValue } })
        {
            using var missing = await client.GetAsync(new Uri(Body(wrong), UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        for (var i = 0; i < 100; i++)
        { runtime.Messages.Capture(Source, Revision, Guid.NewGuid(), "{}"u8); }
        using var evicted = await client.GetAsync(new Uri(Body(old), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, evicted.StatusCode);
        var page = await client.GetJsonAsync(Root + "?" + Query(), Token);
        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CatalogPagingIsVersionBoundAndDeletedObjectsCannotBeQueried()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var runtime = await Start(factory, 105);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var first = await client.GetJsonAsync(Root + "/objects", Token);
        Assert.Equal(100, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var query = $"?revision={Revision.ToString(CultureInfo.InvariantCulture)}&after={Uri.EscapeDataString(cursor)}";
        var next = await client.GetJsonAsync(Root + "/objects" + query, Token);
        Assert.Equal(5, next.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
        var quiet = await runtime.QuiesceInputsAsync(Token);
        await runtime.ActivateCommittedAsync(runtime.Prepare(new(Revision + 1, [])), quiet);
        using var changed = await client.GetAsync(new Uri(Root + "/objects" + query, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var removed = await client.GetAsync(new Uri(Root + "?" + Query(RuntimeUnitId.Create("input.simulator", "meter000/values"), Revision + 1), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Empty(runtime.GetMessageCatalog().Objects);
        Assert.Equal(0, runtime.Messages.Status.Objects);
    }

    [Fact]
    public async Task RetainedRecordKeepsItsOldRevisionAndNeverMatchesAnotherRuntimeIncarnation()
    {
        RuntimeMessageRecord original;
        await using (var factory = new TenonEdgeApplicationFactory())
        {
            var runtime = await Start(factory);
            original = runtime.Messages.Read(Source, false)[0];
            TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
            using var client = await factory.CreateLoggedInClientAsync("operator", Token);
            var quiet = await runtime.QuiesceInputsAsync(Token);
            await runtime.ActivateCommittedAsync(runtime.Prepare(new(Revision + 1, [Input(Source)])), quiet);
            var result = await client.GetJsonAsync(Body(original, Revision + 1), Token);
            Assert.Equal(Revision.ToString(CultureInfo.InvariantCulture), result.GetProperty("record").GetProperty("revision").GetString());
            using var changed = await client.GetAsync(new Uri(Body(original), UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        }
        await using var restarted = new TenonEdgeApplicationFactory();
        var current = await Start(restarted);
        Assert.Equal(original.Sequence, current.Messages.Read(Source, false)[0].Sequence);
        TestAccounts.Create(restarted.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var newClient = await restarted.CreateLoggedInClientAsync("operator", Token);
        using var missing = await newClient.GetAsync(new Uri(Body(original), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("/objects?limit=101")]
    [InlineData("/objects?limit=0")]
    [InlineData("/objects?revision=-1")]
    [InlineData("/objects?after=input.simulator%2Fmeter%2Fvalues")]
    [InlineData("/objects?revision=0&after=invalid")]
    [InlineData("?kind=input.simulator&key=bad%2F%2Fkey&revision=1")]
    [InlineData("?kind=input.simulator&key=meter%2Fvalues&revision=0")]
    public async Task InvalidQueriesFailWithoutConstructingRuntimeUnits(string query)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Root + query, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<RuntimeHost>().ActiveRevision);
    }
}

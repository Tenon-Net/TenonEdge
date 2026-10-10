using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteRecentPreviewTests
{
    private const string Endpoint = "/api/edge-admin/v1/routes/preview/recent";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly RuntimeUnitId Source = RuntimeUnitId.Create("input.simulator", "sim/values");
    private static RouteRecentMessagesRequest Listing(RoutePreviewRequest request) => new(request.ExpectedVersion, request.Graph, request.SourceId);
    private static RouteRecentPreviewRequest Selection(RoutePreviewRequest request, RuntimeMessageRecord record) =>
        new(request.ExpectedVersion, request.Graph, request.SourceId, record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), record.SourceMessageId);

    private static async Task<RuntimeHost> StartAsync(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<RuntimeHost>();
        var unit = new RuntimeUnitPlan(Source, JsonSerializer.Deserialize<JsonElement>("""{"periodMs":86400000,"sample":{"value":1}}"""));
        await runtime.ActivateAsync(runtime.Prepare(new RuntimePlan(7, [unit])));
        await RuntimeTesting.ReadCapturedAsync(runtime, Source);
        return runtime;
    }

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RealRuntimeCaptureCanBeListedAndPreviewedWithoutNewSamplingOrOutputs(string role, HttpStatusCode expected)
    {
        await using var factory = new RoutePreviewTests.NoOutboundFactory();
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        var runtime = await StartAsync(factory.Services);
        var record = Assert.Single(runtime.Messages.Read(Source, false));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var listing = await client.PostJsonAsync(Endpoint + "/messages", Listing(request), Token);
        Assert.Equal(expected, listing.StatusCode);
        using var preview = await client.PostJsonAsync(Endpoint, Selection(request, record), Token);
        Assert.Equal(expected, preview.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var json = await listing.ReadJsonAsync(Token);
            Assert.True(json.GetProperty("running").GetBoolean());
            Assert.Equal("7", json.GetProperty("activeRevision").GetString());
            var entry = Assert.Single(json.GetProperty("records").EnumerateArray());
            Assert.True(entry.GetProperty("executable").GetBoolean());
            Assert.False(entry.TryGetProperty("body", out _));
            Assert.False(entry.TryGetProperty("values", out _));
            Assert.Equal(record.SourceMessageId, entry.GetProperty("sourceMessageId").GetGuid());
            var result = await preview.ReadJsonAsync(Token);
            Assert.True(result.GetProperty("previewed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("invalidUpstreamTime").ValueKind);
            Assert.Equal(record.SourceMessageId, result.GetProperty("result").GetProperty("sourceMessageId").GetGuid());
            Assert.Equal("1", result.GetProperty("result").GetProperty("source").GetProperty("fields")[0].GetProperty("text").GetString());
            Assert.True((await preview.Content.ReadAsByteArrayAsync(Token)).Length <= 65536);
        }
        Assert.Single(runtime.Messages.Read(Source, false));
        Assert.Equal(0, factory.Connector.Calls);
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Empty(factory.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
        Assert.Equal(request.ExpectedVersion, factory.Services.GetRequiredService<RouteDraftStore>().Read().Version);
        Assert.Equal(7, runtime.ActiveRevision);
    }

    [Fact]
    public async Task PrivacyBlockedSampleHasMetadataButCannotBeReplayed()
    {
        await using var factory = new RoutePreviewTests.NoOutboundFactory();
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        var runtime = await StartAsync(factory.Services);
        var now = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "values", "sim", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("fixture-secret") },
            diagnosticAccess: DiagnosticSampleAccess.Redacted);
        runtime.Messages.CaptureInput(Source, 7, message);
        var record = runtime.Messages.Read(Source, true)[0];
        Assert.Null(record.Body);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var listing = await client.PostJsonAsync(Endpoint + "/messages", Listing(request), Token);
        var entry = (await listing.ReadJsonAsync(Token)).GetProperty("records")[0];
        Assert.True(entry.GetProperty("privacyBlocked").GetBoolean());
        Assert.False(entry.GetProperty("executable").GetBoolean());
        Assert.False(entry.GetProperty("truncated").GetBoolean());
        using var response = await client.PostJsonAsync(Endpoint, Selection(request, record), Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("fixture-secret", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, true, true, 1)]
    public async Task RecentPreviewReturnsCapturedMetadataWithoutReinferring(bool invalid, bool retained, bool duplicate, int qos)
    {
        await using var factory = new RoutePreviewTests.NoOutboundFactory();
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        var runtime = await StartAsync(factory.Services);
        var now = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "values", "sim", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) },
            sampleMetadata: new(invalid, new(retained, duplicate, qos)));
        runtime.Messages.CaptureInput(Source, 7, message); // metadata fixture, not a simulator MQTT acquisition claim
        var record = runtime.Messages.Read(Source, false)[0];
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, Selection(request, record), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadJsonAsync(Token);
        Assert.Equal(invalid, result.GetProperty("invalidUpstreamTime").GetBoolean());
        var mqtt = result.GetProperty("mqtt");
        Assert.Equal(retained, mqtt.GetProperty("retained").GetBoolean());
        Assert.Equal(duplicate, mqtt.GetProperty("duplicate").GetBoolean());
        Assert.Equal(qos, mqtt.GetProperty("qos").GetInt32());
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Fact]
    public async Task ExactTypedFixtureReplaysWithoutManualJsonReinterpretation()
    {
        await using var factory = new RoutePreviewTests.NoOutboundFactory();
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        var runtime = await StartAsync(factory.Services);
        var now = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "values", "sim", now, TimeSource.GatewayUnsynced, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromUInt64(ulong.MaxValue) });
        runtime.Messages.CaptureInput(Source, 7, message); // typed software fixture, not a simulator uint64 acquisition claim
        var record = runtime.Messages.Read(Source, false)[0];
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, Selection(request, record), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.ReadJsonAsync(Token)).GetProperty("result");
        Assert.Equal("18446744073709551615", result.GetProperty("source").GetProperty("fields")[0].GetProperty("text").GetString());
        Assert.Equal("gateway-unsynced", result.GetProperty("timeSource").GetString());
        Assert.Contains("18446744073709551615", result.GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStoppedWrongRevisionIdentityTruncatedAndEvictedSelectionsNeverFallback()
    {
        await using var factory = new RoutePreviewTests.NoOutboundFactory();
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var empty = await client.PostJsonAsync(Endpoint + "/messages", Listing(request), Token);
        var emptyJson = await empty.ReadJsonAsync(Token);
        Assert.False(emptyJson.GetProperty("running").GetBoolean());
        Assert.Empty(emptyJson.GetProperty("records").EnumerateArray());
        var runtime = await StartAsync(factory.Services);
        var record = Assert.Single(runtime.Messages.Read(Source, false));
        var selection = Selection(request, record);
        foreach (var invalid in new[] { selection with { Revision = "8" }, selection with { SourceMessageId = Guid.NewGuid() },
            selection with { Sequence = "999" } })
        {
            using var response = await client.PostJsonAsync(Endpoint, invalid, Token);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("preview.recent.unavailable", await response.ReadProblemCodeAsync(Token));
        }
        runtime.Messages.Capture(Source, 7, Guid.NewGuid(), new byte[70000]); // valid UTF-8 prefix, deliberately truncated
        var truncated = runtime.Messages.Read(Source, false)[0];
        using var unavailable = await client.PostJsonAsync(Endpoint, Selection(request, truncated), Token);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        using var metadata = await client.PostJsonAsync(Endpoint + "/messages", Listing(request), Token);
        var entry = (await metadata.ReadJsonAsync(Token)).GetProperty("records")[0];
        Assert.True(entry.GetProperty("truncated").GetBoolean());
        Assert.False(entry.GetProperty("executable").GetBoolean());
        for (var i = 0; i < 101; i++)
        { runtime.Messages.Capture(Source, 7, Guid.NewGuid(), "{}"u8); }
        using var evicted = await client.PostJsonAsync(Endpoint, selection, Token);
        Assert.Equal(HttpStatusCode.Conflict, evicted.StatusCode);
        await runtime.StopAsync(Token);
        using var stopped = await client.PostJsonAsync(Endpoint, selection, Token);
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        using var stoppedList = await client.PostJsonAsync(Endpoint + "/messages", Listing(request), Token);
        var stoppedJson = await stoppedList.ReadJsonAsync(Token);
        Assert.False(stoppedJson.GetProperty("running").GetBoolean());
        Assert.All(stoppedJson.GetProperty("records").EnumerateArray(), item => Assert.False(item.GetProperty("executable").GetBoolean()));
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredDeadlineReleasesQuotaWithoutSampling(bool list)
    {
        var time = new RoutePreviewTests.DeadlineTimeProvider();
        await using var factory = new RoutePreviewTests.NoOutboundFactory(time);
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var endpoint = Endpoint + (list ? "/messages" : "");
        object body = list ? Listing(request) : new RouteRecentPreviewRequest(request.ExpectedVersion, request.Graph,
            request.SourceId, "1", "7", Guid.NewGuid());
        for (var i = 0; i < 3; i++)
        {
            time.ExpireNext = true;
            using var response = await client.PostJsonAsync(endpoint, body, Token);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal("preview.deadline", await response.ReadProblemCodeAsync(Token));
        }
        using var available = await client.PostJsonAsync(endpoint, body, Token);
        Assert.Equal(list ? HttpStatusCode.OK : HttpStatusCode.Conflict, available.StatusCode);
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothEndpointsRequireSessionCsrfCurrentGraphSourceAndSharedAdmission(bool list)
    {
        await using var factory = new RoutePreviewTests.NoOutboundFactory();
        var request = RoutePreviewTests.Seed(factory.Services, 1);
        var runtime = await StartAsync(factory.Services);
        var selection = Selection(request, Assert.Single(runtime.Messages.Read(Source, false)));
        var endpoint = Endpoint + (list ? "/messages" : "");
        object Body(string version, long source) => list
            ? Listing(request) with { ExpectedVersion = version, SourceId = source }
            : selection with { ExpectedVersion = version, SourceId = source };
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(endpoint, Body(request.ExpectedVersion, request.SourceId), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var stale = await client.PostJsonAsync(endpoint, Body("stale", request.SourceId), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(endpoint, Body(request.ExpectedVersion, request.SourceId + 1), Token);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        var leases = Enumerable.Range(0, 4).Select(i => budget.TryAcquire("fixture" + i)!).ToArray();
        try
        {
            using var busy = await client.PostJsonAsync(endpoint, Body(request.ExpectedVersion, request.SourceId), Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        using var success = await client.PostJsonAsync(endpoint, Body(request.ExpectedVersion, request.SourceId), Token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(endpoint, Body(request.ExpectedVersion, request.SourceId), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Equal(0, factory.Connector.Calls);
    }
}

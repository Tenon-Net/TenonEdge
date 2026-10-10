using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RoutePreviewTests
{
    private const string Endpoint = "/api/edge-admin/v1/routes/preview";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static RoutePreviewRequest Seed(IServiceProvider services, int port)
    {
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "http", "HTTP", ConnectionKind.Http, Http: new($"http://127.0.0.1:{port}/")), "test", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "sim", "Sim", DeviceKind.Simulator), "test", null);
        var source = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Simulator: new(SampleJson: "{\"value\":1}")), "test", null);
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { source.Input.Id } })),
                new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))], [new("in", "out")], new([]));
        return new(source.Version, graph, source.Input.Id, "{\"value\":9223372036854775807}");
    }

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task ManualSamplePreviewIsAuthorizedExactAndNeverConnectsWritesDraftsOrEnqueues(string role, HttpStatusCode expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var factory = new NoOutboundFactory();
        var request = Seed(factory.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var raw = await response.Content.ReadAsByteArrayAsync(Token);
            Assert.True(raw.Length <= 64 * 1024);
            using var json = JsonDocument.Parse(raw);
            Assert.True(json.RootElement.GetProperty("previewed").GetBoolean());
            var result = json.RootElement.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
            Assert.Equal("9223372036854775807", result.GetProperty("source").GetProperty("fields")[0].GetProperty("text").GetString());
            var body = result.GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString();
            Assert.Contains("9223372036854775807", body, StringComparison.Ordinal);
        }
        Assert.False(listener.Pending());
        Assert.Equal(0, factory.Connector.Calls);
        var drafts = factory.Services.GetRequiredService<RouteDraftStore>().Read();
        Assert.Equal(request.ExpectedVersion, drafts.Version);
        Assert.Empty(drafts.Routes);
        var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PreviousRevision);
        Assert.Null(state.PendingRevision);
        Assert.Equal(0, factory.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
    }

    [Fact]
    public async Task AnonymousCsrfStaleForeignAndMalformedSamplesAreRejectedAndDoNotSend()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var factory = new NoOutboundFactory();
        var request = Seed(factory.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var stale = await client.PostJsonAsync(Endpoint, request with { ExpectedVersion = "stale" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(Endpoint, request with { SourceId = request.SourceId + 1 }, Token);
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        var problem = await foreign.ReadJsonAsync(Token);
        Assert.False(problem.GetProperty("previewed").GetBoolean());
        Assert.Equal("source.missing", problem.GetProperty("issues")[0].GetProperty("code").GetString());
        foreach (var sample in new[] { "{", "[]", "{\"value\":\"" + new string('界', 22000) + "\"}" })
        {
            using var invalid = await client.PostJsonAsync(Endpoint, request with { SampleJson = sample }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var oversized = await client.PostJsonAsync(Endpoint, request with { SampleJson = new string('x', 1024 * 1024) }, Token);
        // TestServer does not implement Kestrel body limits; the explicit sample bound still rejects it.
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.False(listener.Pending());
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewSharesSessionAndGlobalAdmissionAndReleasesQuotaAfterInvalidRequests(bool global)
    {
        await using var factory = new NoOutboundFactory();
        var request = Seed(factory.Services, 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        var leases = Enumerable.Range(0, global ? 4 : 2).Select(i => budget.TryAcquire(global ? "other" + i : key)!).ToArray();
        try
        {
            using var busy = await client.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        for (var i = 0; i < 3; i++)
        {
            using var invalid = await client.PostJsonAsync(Endpoint, request with { SampleJson = "{" }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var success = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }
    [Fact]
    public async Task ExpiredDeadlineReturns504AndReleasesAdmissionWithoutSending()
    {
        var time = new DeadlineTimeProvider();
        await using var factory = new NoOutboundFactory(time);
        var request = Seed(factory.Services, 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        for (var i = 0; i < 3; i++)
        {
            time.ExpireNext = true;
            using var expired = await client.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.GatewayTimeout, expired.StatusCode);
            Assert.Equal("preview.deadline", await expired.ReadProblemCodeAsync(Token));
        }
        using var success = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Fact]
    public async Task MappingDataFailureRemainsPathLocalInTheApiResponse()
    {
        await using var factory = new NoOutboundFactory();
        var request = Seed(factory.Services, 1);
        var outputConfig = request.Graph.Nodes[1].Config;
        request = request with
        {
            SampleJson = "{}",
            Graph = request.Graph with
            {
                Nodes = [.. request.Graph.Nodes,
                new("bad", "transform.map", 1, "Bad", JsonSerializer.Deserialize<JsonElement>("""{"rules":[{"target":"required","sourceField":"value","type":"integer","required":true}]}""")),
                new("other", "output.http", 1, "Other", outputConfig)],
                Edges = [.. request.Graph.Edges, new("in", "bad"), new("bad", "other")],
            }
        };
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.ReadJsonAsync(Token);
        Assert.True(json.GetProperty("previewed").GetBoolean());
        var result = json.GetProperty("result");
        Assert.Equal(2, result.GetProperty("totalPaths").GetInt32());
        Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
        var failed = result.GetProperty("paths").EnumerateArray().Single(path => !path.GetProperty("succeeded").GetBoolean());
        Assert.Equal("bad", failed.GetProperty("issues")[0].GetProperty("nodeId").GetString());
        Assert.Equal("mapping.required", failed.GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.Equal(0, factory.Connector.Calls);
    }

    [Fact]
    public async Task WholeResponseIncludingEnvelopeStaysBelow64KiBWhenOutputIsTruncated()
    {
        await using var factory = new NoOutboundFactory();
        var request = Seed(factory.Services, 1);
        var connectionId = request.Graph.Nodes[1].Config.GetProperty("connectionId").GetInt64();
        request.Graph.Nodes[1] = request.Graph.Nodes[1] with
        {
            Config = JsonSerializer.SerializeToElement(new
            {
                connectionId,
                payload = new { mode = "custom", fields = new[] { new { target = "large", constant = string.Concat(Enumerable.Repeat("😀", 12000)) } } },
            })
        };
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.True(bytes.Length <= 64 * 1024);
        using var json = JsonDocument.Parse(bytes);
        var result = json.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("paths")[0].GetProperty("output").GetProperty("truncated").GetBoolean());
        Assert.Equal(0, factory.Connector.Calls);
    }

    internal sealed class DeadlineTimeProvider : TimeProvider
    {
        internal bool ExpireNext { get; set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (ExpireNext && dueTime == TimeSpan.FromSeconds(30))
            {
                ExpireNext = false;
                callback(state);
                return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    internal sealed class NoOutboundFactory(TimeProvider? time = null) : TenonEdgeApplicationFactory
    {
        internal NoOutboundConnector Connector { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IOutboundConnector>(Connector);
                if (time is not null)
                { services.AddSingleton(time); }
            });
        }
    }
    internal sealed class NoOutboundConnector : IOutboundConnector
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromException<Stream>(new InvalidOperationException("Preview must never connect."));
        }
    }

}

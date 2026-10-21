using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Credentials;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ExtensionOutputCredentialTests
{
    private const string Api = "/api/edge-admin/v1";
    private const string FirstSecret = "fixture-authwire-first";
    private const string NextSecret = "fixture-authwire-next";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task CredentialReviewAndRotationResumeFrozenDeliveryButNeverOverrideManualPause(string role)
    {
        using var data = TemporaryDirectory.Create();
        using var peer = Listen();
        using var samples = new SemaphoreSlim(0);
        var port = ((IPEndPoint)peer.LocalEndpoint).Port;
        await using var host = Host(data.Path, samples, port);
        var seed = Seed(host.Services, port);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        TestAccounts.Create(host.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        using var admin = await host.CreateLoggedInClientAsync("admin", Token);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        using var missing = await client.PostJsonAsync(Api + "/configuration/review", new ConfigurationReviewRequest(seed.Version), Token);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        // Static review is intentionally not runtime preparation. Missing required credentials
        // must stop the confirmed operation before COMMIT, not be reported as an active version.
        var staticReview = await missing.ReadJsonAsync(Token);
        using var rejected = await client.PostJsonAsync(Api + "/configuration/apply",
            new ConfigurationApplicationRequest(seed.Version, null, staticReview.GetProperty("planHash").GetString()!, true), Token);
        Assert.Equal(HttpStatusCode.Accepted, rejected.StatusCode);
        var rejectedId = (await rejected.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(rejectedId)?.FinishedAt is not null);
        var failed = await client.GetJsonAsync(Api + $"/configuration/operations/{rejectedId}", Token);
        Assert.Equal("failed", failed.GetProperty("stage").GetString());
        Assert.False(failed.GetProperty("committed").GetBoolean());
        Assert.False(failed.GetProperty("result").GetProperty("activated").GetBoolean());
        Assert.Null(versions.ReadPointers().EffectiveRevision);
        Assert.Equal(0, transport.Builds);
        Assert.False(peer.Pending());
        var first = await WriteCredential(client, seed, null, FirstSecret);
        Assert.Equal(2, await Apply(client, host.Services, seed.Version));
        var snapshot = versions.Load(2)!;
        samples.Release();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var socket = await peer.AcceptTcpClientAsync(deadline.Token);
        var frame = await ReadFrame(socket.GetStream(), deadline.Token);
        Assert.Equal(FirstSecret, frame.Secret);
        Assert.Equal("events/meter", frame.Address);
        Assert.Contains("\"value\":9007199254740993", Encoding.UTF8.GetString(frame.Payload), StringComparison.Ordinal);
        var original = Assert.Single(runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10));
        Assert.Equal(original.Payload.ToArray(), frame.Payload);
        await socket.GetStream().WriteAsync(new byte[] { 21 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().Single().Sender.State == "automaticallyPaused");
        using var review = await client.PostJsonAsync(CredentialPath(seed.ConnectionId) + "/review",
            new ModuleCredentialReviewRequest(seed.Version, first.Identity.Version, "wire-user"), Token);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var comparison = await review.ReadJsonAsync(Token);
        Assert.True(comparison.GetProperty("accountDeclarationRequired").GetBoolean());
        Assert.Equal(1, comparison.GetProperty("pendingCount").GetInt64());
        Assert.DoesNotContain(FirstSecret, comparison.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, socket.Available);
        using var unspecified = await client.PutAsJsonAsync(CredentialPath(seed.ConnectionId),
            new ModuleCredentialRequest(seed.Version, first.Identity.Version, new() { ["auth"] = NextSecret }, "wire-user"), Token);
        Assert.Equal(HttpStatusCode.Conflict, unspecified.StatusCode);
        Assert.Equal("credential.accountDeclarationRequired", await unspecified.ReadProblemCodeAsync(Token));
        var second = await WriteCredential(client, seed, first.Identity.Version, NextSecret, changed: false);
        Assert.Equal(first.Identity.AccountMarker, second.Identity.AccountMarker);
        Assert.NotEqual(first.Identity.Version, second.Identity.Version);
        var resumed = await ReadFrame(socket.GetStream(), deadline.Token);
        Assert.Equal(NextSecret, resumed.Secret);
        Assert.Equal(frame.Address, resumed.Address);
        Assert.Equal(frame.Payload, resumed.Payload);
        var stillPending = Assert.Single(runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10));
        Assert.Equal(original.Seq, stillPending.Seq);
        Assert.Equal(original.DeliveryId, stillPending.DeliveryId);
        Assert.Equal(original.Fingerprint, stillPending.Fingerprint);
        await socket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10).Count == 0);

        var pause = await Control(admin, seed.ConnectionId, true);
        samples.Release();
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10).Count == 1 &&
            runtime.GetOutputSenders().Single().Sender.State == "manuallyPaused");
        var third = await WriteCredential(client, seed, second.Identity.Version, "fixture-authwire-third", changed: false);
        Assert.Equal(first.Identity.AccountMarker, third.Identity.AccountMarker);
        Assert.True((await admin.GetJsonAsync(Api + $"/connections/{seed.ConnectionId}/delivery-control", Token)).GetProperty("manuallyPaused").GetBoolean());
        Assert.Equal("manuallyPaused", runtime.GetOutputSenders().Single().Sender.State);
        Assert.Equal(0, socket.Available);
        _ = await Control(admin, seed.ConnectionId, false, pause.ResumeVersion);
        var next = await ReadFrame(socket.GetStream(), deadline.Token);
        Assert.Equal("fixture-authwire-third", next.Secret);
        Assert.NotEqual(Encoding.UTF8.GetString(frame.Payload), Encoding.UTF8.GetString(next.Payload));
        await socket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(seed.ConnectionId, 0, 10).Count == 0);
        Assert.Empty(runtime.Deliveries.ReadQuarantined(seed.ConnectionId, 0, 10));
        Assert.Equal(1, transport.Builds);
        Assert.Equal(1, transport.Adapters);
        Assert.Equal(1, transport.Sessions);
        Assert.False(peer.Pending());
        Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(snapshot.ContentHash, versions.Load(2)!.ContentHash);
        Assert.Equal(seed.Version, host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        var audits = TestAccounts.Audit(host.Services, "credential.module.write");
        Assert.Equal(3, audits.Count);
        foreach (var secret in new[] { FirstSecret, NextSecret, "fixture-authwire-third" })
        {
            Assert.DoesNotContain(secret, snapshot.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(audits), StringComparison.Ordinal);
        }
    }

    private static FixedDataDirectoryApplicationFactory Host(string path, SemaphoreSlim samples, params int[] ports) => new(path,
        ports.Select((port, index) => (port, index)).ToDictionary(pair => $"TenonEdge:Outbound:LocalPorts:{pair.index}", pair => pair.port.ToString(CultureInfo.InvariantCulture)),
        services => services.AddSingleton(ModuleCredentialTests.Definition)
            .AddSingleton<IOutputModuleFactory, TestAuthenticatedWireOutputModule>()
            .AddSingleton<IInputModuleFactory>(new TestScalarInputModule { UsesDurableHandoff = true, Samples = samples })
            .AddSingleton(provider => new TestWireOutputTransport(provider.GetRequiredService<ISharedConnections>(),
                provider.GetRequiredService<IOutboundConnector>(), provider.GetRequiredService<IModuleCredentialResolver>())
            { Protocol = "authwire", CredentialField = "auth" })
            .AddSingleton<IOutputAdapterFactory>(provider => provider.GetRequiredService<TestWireOutputTransport>()));

    private sealed record Setup(string Version, long ConnectionId);
    private static Setup Seed(IServiceProvider services, int port)
    {
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, ModuleCredentialTests.Draft(connections.Read().Version, port), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Module, ModuleKind: "input.testscalar"), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "source", "Source", Module: new("input.testscalar", 3, JsonSerializer.Deserialize<JsonElement>("""{"value":9007199254740993}"""))), "fixture", null);
        var graph = new RouteGraph(1,
            [new("in", "input.testscalar", 3, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("out", "output.authwiremodule", 1, "Output", JsonSerializer.SerializeToElement(new
             { connectionId = connection.Connection.Id, parameters = new { address = "events/{deviceKey}" }, connections = new { connection = connection.Connection.Id } }))],
            [new("in", "out")], new([]));
        var route = services.GetRequiredService<RouteDraftStore>().Write(null, new(input.Version, "route", "Route", true, graph), "fixture", null);
        return new(route.Version, connection.Connection.Id);
    }

    private static string CredentialPath(long id) => Api + $"/connections/{id}/module-credential";
    private static async Task<ModuleCredentialMetadata> WriteCredential(HttpClient client, Setup setup, string? previousVersion, string secret,
        bool? changed = null, string account = "wire-user")
    {
        using var response = await client.PutAsJsonAsync(CredentialPath(setup.ConnectionId),
            new ModuleCredentialRequest(setup.Version, previousVersion, new() { ["auth"] = secret }, account, changed), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<ModuleCredentialMetadata>(raw, JsonSerializerOptions.Web)!;
    }
    private static async Task<long> Apply(HttpClient client, IServiceProvider services, string version)
    {
        using var reviewed = await client.PostJsonAsync(Api + "/configuration/review", new ConfigurationReviewRequest(version), Token);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var review = await reviewed.ReadJsonAsync(Token);
        Assert.True(review.GetProperty("preflightPassed").GetBoolean(), review.GetRawText());
        using var accepted = await client.PostJsonAsync(Api + "/configuration/apply", new ConfigurationApplicationRequest(version,
            review.GetProperty("effectiveRevision").ValueKind == JsonValueKind.Null ? null : review.GetProperty("effectiveRevision").GetInt64(),
            review.GetProperty("planHash").GetString()!, true), Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var operation = (await accepted.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        var coordinator = services.GetRequiredService<RuntimeCoordinator>();
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(operation)?.FinishedAt is not null);
        var result = await client.GetJsonAsync(Api + $"/configuration/operations/{operation}", Token);
        Assert.Equal("completed", result.GetProperty("stage").GetString());
        Assert.True(result.GetProperty("committed").GetBoolean());
        Assert.True(result.GetProperty("result").GetProperty("activated").GetBoolean());
        return result.GetProperty("revision").GetInt64();
    }
    private static async Task<OutputControlState> Control(HttpClient client, long id, bool paused, string? version = null)
    {
        using var response = await client.PutAsJsonAsync(Api + $"/connections/{id}/delivery-control", new OutputControlRequest(paused, true, version), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OutputControlState>(JsonSerializerOptions.Web, Token))!;
    }
    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
    private sealed record Frame(string Secret, string Address, byte[] Payload)
    {
        public override string ToString() => "Test frame (material and body omitted)";
    }
    private static async Task<Frame> ReadFrame(Stream stream, CancellationToken token)
    {
        var secretLength = new byte[4];
        await stream.ReadExactlyAsync(secretLength, token);
        var size = BinaryPrimitives.ReadInt32BigEndian(secretLength);
        Assert.InRange(size, 1, 128 * 1024);
        var secret = new byte[size];
        await stream.ReadExactlyAsync(secret, token);
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var addressBytes = BinaryPrimitives.ReadInt32BigEndian(header);
        var bodyBytes = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        Assert.InRange(addressBytes, 0, 4096);
        Assert.InRange(bodyBytes, 1, 256 * 1024);
        var address = new byte[addressBytes];
        var body = new byte[bodyBytes];
        await stream.ReadExactlyAsync(address, token);
        await stream.ReadExactlyAsync(body, token);
        return new(Encoding.UTF8.GetString(secret), Encoding.UTF8.GetString(address), body);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class HttpReadPreviewApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Endpoint(long id) => $"/api/edge-admin/v1/devices/{id}/preview/http";
    private static HttpSourceDraft Source(string path = "values") => new(path,
        [new("value", "/value", FieldType.Integer)]);

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task RolesGuardActualWireReadsAndPreviewNeverWritesDraft(string role, HttpStatusCode expected)
    {
        using var slave = new TcpListener(IPAddress.Loopback, 0);
        slave.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = Setup(factory.Services, ((IPEndPoint)slave.LocalEndpoint).Port);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var peer = expected == HttpStatusCode.OK ? Reply(slave, ["unsaved"]) : Task.CompletedTask;
        using var response = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version, input, Source("unsaved")), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = (await response.Content.ReadFromJsonAsync<HttpDevicePreviewResult>(Token))!;
            Assert.Equal("fast", Assert.Single(result.Sources).Key);
            Assert.Equal("9223372036854775807", Assert.Single(result.Sources[0].Preview.Fields).Text);
            Assert.True(result.Sources[0].Preview.CanUseAsSample);
        }
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(slave.Pending());
        var drafts = factory.Services.GetRequiredService<InputDraftStore>().Read(device);
        Assert.Equal(version, drafts.Version);
        Assert.Equal("values", Assert.Single(drafts.Inputs).Http!.Path);
    }

    [Fact]
    public async Task DeviceSelectionPreservesSourcesAndOnePhysicalConnection()
    {
        using var slave = new TcpListener(IPAddress.Loopback, 0);
        slave.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, _, version) = Setup(factory.Services, ((IPEndPoint)slave.LocalEndpoint).Port);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var second = store.Write(device, null, new(version, "slow", "Slow", Http: Source("second") with { Fields = [new("other", "/value", FieldType.Integer)] }), "fixture", null);
        var peer = Reply(slave, ["values", "second"]);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(second.Version), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<HttpDevicePreviewResult>(Token))!;
        Assert.Equal(2, result.TotalSources);
        Assert.Equal(2, result.Sources.Length);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(slave.Pending());
        Assert.Equal(second.Version, store.Read(device).Version);
    }

    [Fact]
    public async Task AnonymousCsrfStaleVersionWrongOwnershipAndInvalidPathsDoNotReachNetwork()
    {
        using var slave = new TcpListener(IPAddress.Loopback, 0);
        slave.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = Setup(factory.Services, ((IPEndPoint)slave.LocalEndpoint).Port);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var stale = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest("stale", input), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version, input + 1, Source()), Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var invalid = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version, input, Source("http://elsewhere.invalid/")), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var secret = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version, input,
            Source() with { Headers = new() { ["Authorization"] = "Bearer fixture-secret" } }), Token);
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.False(slave.Pending());
    }

    [Fact]
    public async Task SharedPreviewQuotaRejectsBeforeReading()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire(key);
        using var second = budget.TryAcquire(key);
        using var response = await client.PostJsonAsync(Endpoint(1), new HttpReadPreviewRequest("0"), Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task SavedCredentialIsUsedOnWireAndEchoIsNotReturnedAsSample()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = Setup(factory.Services, ((IPEndPoint)listener.LocalEndpoint).Port);
        var connection = Assert.Single(factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections);
        factory.Services.GetRequiredService<HttpCredentialStore>().Write(connection.Id,
            new(connection.Http!.BaseAddress, HttpCredentialKind.Bearer, Secret: "fixture-not-logged", ExpectedDraftVersion: version), "fixture", null, false);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Assert.Equal("GET /values HTTP/1.1", await reader.ReadLineAsync(Token));
            var headers = new List<string>();
            while (await reader.ReadLineAsync(Token) is { Length: > 0 } line)
            { headers.Add(line); }
            Assert.Contains("Authorization: Bearer fixture-not-logged", headers);
            const string body = "{\"echo\":\"fixture-not-logged\",\"value\":42}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}"), Token);
        }, Token);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint(device), new HttpReadPreviewRequest(version, input), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = Assert.Single((await response.Content.ReadFromJsonAsync<HttpDevicePreviewResult>(Token))!.Sources).Preview;
        Assert.True(preview.Redacted);
        Assert.False(preview.CanUseAsSample);
        Assert.Null(preview.RawJson);
        Assert.Empty(preview.Fields);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static (long Device, long Input, string Version) Setup(IServiceProvider services, int port)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, new(connections.Read().Version, "plc", "PLC", ConnectionKind.Http,
            Http: new($"http://127.0.0.1:{port}/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "device", "Device", DeviceKind.Http, ConnectionId: connection.Connection.Id), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "fast", "Fast", Http: Source()), "fixture", null);
        return (device.Device.Id, input.Input.Id, input.Version);
    }
    private static async Task Reply(TcpListener listener, string[] paths)
    {
        using var socket = await listener.AcceptTcpClientAsync(Token);
        var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        foreach (var path in paths)
        {
            Assert.Equal($"GET /{path} HTTP/1.1", await reader.ReadLineAsync(Token));
            while (await reader.ReadLineAsync(Token) is { Length: > 0 })
            { }
            const string body = "{\"value\":9223372036854775807}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}"), Token);
        }
    }

}

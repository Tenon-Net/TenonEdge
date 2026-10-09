using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class ModbusReadPreviewApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Endpoint(long id) => $"/api/edge-admin/v1/devices/{id}/preview/modbus";
    private static ModbusSourceDraft Source(int address = 0) => new(1000,
        [new() { Key = "value", FunctionCode = 3, Address = address, Type = "uint64", ByteOrder = "ABCD" }]);

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task RolesGuardActualWireReadsAndPreviewNeverWritesDraft(string role, HttpStatusCode expected)
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = Setup(factory.Services, slave.Endpoint.Port);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest(version, input, Source(20)), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = (await response.Content.ReadFromJsonAsync<ModbusDevicePreviewResult>(Token))!;
            Assert.Equal("fast", Assert.Single(result.Groups).Key);
            Assert.Equal(20, Assert.Single(result.Groups[0].Preview.Points).Offset);
            Assert.Equal("integer", result.Groups[0].Preview.Points[0].Value.Type);
            Assert.Equal(1, slave.RequestCount);
        }
        else
        { Assert.Equal(0, slave.ConnectionCount); }
        var drafts = factory.Services.GetRequiredService<InputDraftStore>().Read(device);
        Assert.Equal(version, drafts.Version);
        Assert.Equal(0, Assert.Single(drafts.Inputs).Modbus!.Points[0].Address);
    }

    [Fact]
    public async Task DeviceSelectionPreservesGroupsAndOnePhysicalConnection()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, _, version) = Setup(factory.Services, slave.Endpoint.Port);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var second = store.Write(device, null, new(version, "slow", "Slow", Modbus: new(2000,
            [Source(4).Points[0] with { Key = "other" }])), "fixture", null);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest(second.Version), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ModbusDevicePreviewResult>(Token))!;
        Assert.Equal(2, result.TotalGroups);
        Assert.Equal(2, result.Groups.Length);
        Assert.Equal(2, slave.RequestCount); // adjacent groups must not merge
        Assert.Equal(1, slave.ConnectionCount);
        Assert.Equal(second.Version, store.Read(device).Version);
    }

    [Fact]
    public async Task AnonymousCsrfStaleVersionWrongOwnershipAndInvalidPointsDoNotReachNetwork()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = Setup(factory.Services, slave.Endpoint.Port);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest(version), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var stale = await client.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest("stale", input), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest(version, input + 1, Source()), Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var invalid = await client.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest(version, input, Source(65535)), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint(device), new ModbusReadPreviewRequest(version), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Equal(0, slave.ConnectionCount);
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
        using var response = await client.PostJsonAsync(Endpoint(1), new ModbusReadPreviewRequest("0"), Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private static (long Device, long Input, string Version) Setup(IServiceProvider services, int port)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, new(connections.Read().Version, "plc", "PLC", ConnectionKind.Modbus,
            Modbus: new("127.0.0.1", port)), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "device", "Device", DeviceKind.Modbus, ConnectionId: connection.Connection.Id, UnitId: 1), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "fast", "Fast", Modbus: Source()), "fixture", null);
        return (device.Device.Id, input.Input.Id, input.Version);
    }
}

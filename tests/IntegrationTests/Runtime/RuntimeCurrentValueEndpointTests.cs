using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeCurrentValueEndpointTests
{
    private const string Root = "/api/edge-admin/v1/runtime/devices";
    private const string Values = Root + "/meter/inputs/main/values";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Configurator")]
    [InlineData("Viewer")]
    public async Task RealInputValuesAreExactPaginatedReadOnlyAndRevisionBound(string role)
    {
        using var data = TemporaryDirectory.Create();
        var seed = Path.Combine(data.Path, "seed.json");
        var fields = string.Join(",", Enumerable.Range(0, 104).Select(i => $"\"p{i:D3}\":{i}"));
        await File.WriteAllTextAsync(seed, """
            {"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/main","configuration":{
            "periodMs":1000,"sample":{
            """ + fields + ",\"z64\":9223372036854775807}}}]}", Token);
        _ = RuntimePlanDocument.Parse(await File.ReadAllBytesAsync(seed, Token));
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed });
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var deniedValues = await anonymous.GetAsync(new Uri(Values + "?revision=1", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedValues.StatusCode);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var runtime = factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime;
        await OutputPipelineTests.UntilAsync(() => runtime.CurrentValues.Status.Fields == 105);
        using var response = await client.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var list = await response.ReadJsonAsync(Token);
        Assert.Equal("normal", list.GetProperty("devices")[0].GetProperty("health").GetString());
        Assert.False(list.GetProperty("devices")[0].GetProperty("removedFromDraft").GetBoolean());
        var first = await client.GetJsonAsync(Values + "?revision=1", Token);
        Assert.Equal(105, first.GetProperty("total").GetInt32());
        Assert.Equal(100, first.GetProperty("items").GetArrayLength());
        Assert.Equal("p099", first.GetProperty("nextKey").GetString());
        var second = await client.GetJsonAsync(Values + "?revision=1&after=p099", Token);
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        var large = second.GetProperty("items")[4].GetProperty("value");
        Assert.Equal("9223372036854775807", large.GetProperty("text").GetString());
        Assert.Equal("integer", large.GetProperty("type").GetString());
        Assert.False(large.GetProperty("isStale").GetBoolean());
        foreach (var query in new[] { "?revision=1&limit=0", "?revision=1&limit=101", "?revision=0", "?revision=1&after=bad/key" })
        {
            using var invalid = await client.GetAsync(new Uri(Values + query, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var changed = await client.GetAsync(new Uri(Values + "?revision=2", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Single(versions.ReadHistory());
        Assert.Empty(TestAccounts.Audit(factory.Services, "runtime.apply"));
    }

    [Fact]
    public async Task UnappliedAndDisabledDevicesAreExplicitAndMetadataUsesSnapshotNotChangedDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Original name", DeviceKind.Simulator, Enabled: false), "operator", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "main", "Sample", Simulator: new(SampleJson: "{\"v\":42}")), "operator", null);
        var before = await client.GetJsonAsync(Root, Token);
        Assert.Equal("notApplied", before.GetProperty("devices")[0].GetProperty("health").GetString());
        await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(input.Version, "operator", null, Token);
        _ = devices.Write(device.Device.Id, new(input.Version, "meter", "Unapplied name", DeviceKind.Simulator), "operator", null);
        var applied = await client.GetJsonAsync(Root, Token);
        Assert.Equal("disabled", applied.GetProperty("devices")[0].GetProperty("health").GetString());
        Assert.Equal("Original name", applied.GetProperty("devices")[0].GetProperty("name").GetString());
        var values = await client.GetJsonAsync(Values + "?revision=1", Token);
        Assert.Equal("v", values.GetProperty("items")[0].GetProperty("key").GetString());
        Assert.Equal("disabled", values.GetProperty("items")[0].GetProperty("value").GetProperty("quality").GetString());
    }

    [Fact]
    public async Task CorruptRunningSnapshotCannotBeReplacedByCurrentDraftMetadata()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync("0", "operator", null, Token);
        var database = factory.Services.GetRequiredService<TenonEdge.Host.Storage.ConfigDatabase>();
        database.Client.Updateable<TenonEdge.Host.Storage.RuntimeSnapshotRecord>().SetColumns(row => row.ContentHash == "damaged")
            .Where(row => row.Revision == 1).ExecuteCommand();
        using var response = await client.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ReadJsonAsync(Token);
        Assert.Equal("runtime.snapshotUnavailable", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("input.mqtt", InputReadHealth.Offline, "push")]
    [InlineData("input.modbus", InputReadHealth.Offline, "offline")]
    [InlineData("input.http", InputReadHealth.Failing, "partialFailure")]
    [InlineData("input.http", InputReadHealth.PartialFailure, "partialFailure")]
    [InlineData("input.http", InputReadHealth.Normal, "normal")]
    public void DeviceHealthDoesNotInferMqttOffline(string kind, InputReadHealth health, string expected)
    {
        var reads = new InputReadSnapshot("meter", "main", health, null, null, null, 0, 0, 0, 0, null, 0, 0);
        var source = new RuntimeDeviceSource("main", "Main", kind, true, kind == "input.mqtt" ? null : 1000, 1, RuntimeUnitState.Running, reads, null);
        Assert.Equal(expected, RuntimeDeviceEndpoints.Health(true, [source]));
        Assert.Equal("disabled", RuntimeDeviceEndpoints.Health(false, [source]));
    }
}

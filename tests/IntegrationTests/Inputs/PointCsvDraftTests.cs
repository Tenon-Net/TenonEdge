using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class PointCsvDraftTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Header = "inputKey,key,name,functionCode,address,addressNotation,type,bit,byteOrder,unit,enabled\n";
    private static string Endpoint(long id) => $"/api/edge-admin/v1/devices/{id}/points/csv/";

    [Theory]
    [InlineData("Overwrite", 1, 1, 0)]
    [InlineData("Incremental", 1, 0, 1)]
    public async Task ImportUpdatesOrSkipsByDeviceKeyPreservesAbsentPointsAndPersistsAfterRestart(string mode, int added, int updated, int skipped)
    {
        using var directory = TemporaryDirectory.Create();
        long deviceId;
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            var setup = Seed(factory);
            deviceId = setup.Id;
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
            using var imported = await admin.PostJsonAsync(Endpoint(deviceId), Request(setup.Version, mode,
                "slow,p,changed,3,40011,manual,uint16,,,℃,false\nslow,new,new,3,20,offset,uint32,,ABCD,kW,true\n"), Token);
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            var result = await imported.ReadJsonAsync(Token);
            Assert.Equal(added, result.GetProperty("added").GetInt32());
            Assert.Equal(updated, result.GetProperty("updated").GetInt32());
            Assert.Equal(skipped, result.GetProperty("skipped").GetInt32());
            version = result.GetProperty("version").GetString()!;
            var inputs = factory.Services.GetRequiredService<InputDraftStore>().Read(deviceId).Inputs;
            Assert.Contains(inputs.Single(input => input.Key == "fast").Modbus!.Points, point => point.Key == "keep");
            var pOwner = inputs.Single(input => input.Modbus!.Points.Any(point => point.Key == "p"));
            Assert.Equal(mode == "Overwrite" ? "slow" : "fast", pOwner.Key);
            Assert.Equal(mode == "Overwrite" ? 10 : 0, pOwner.Modbus!.Points.Single(point => point.Key == "p").Address);
            var edits = factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(ConfigurationEditJournal.Read);
            Assert.Equal("admin", edits.Single(edit => edit.Kind == "input" && edit.Id == inputs.Single(input => input.Key == "slow").Id).UpdatedBy);
            Assert.Equal(mode == "Overwrite" ? "admin" : "fixture", edits.Single(edit => edit.Kind == "input" && edit.Id == inputs.Single(input => input.Key == "fast").Id).UpdatedBy);
            var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
            Assert.Null(state.EffectiveRevision);
            Assert.Null(state.PendingRevision);
            Assert.Single(TestAccounts.Audit(factory.Services, "input.pointsImported"));
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        var saved = restarted.Services.GetRequiredService<InputDraftStore>().Read(deviceId);
        Assert.Equal(version, saved.Version);
        Assert.Equal(3, saved.Inputs.Sum(input => input.Modbus!.Points.Length));
    }

    [Fact]
    public async Task RowErrorsMissingGroupsStaleVersionAndAuditFailureNeverPartiallyWrite()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var setup = Seed(factory);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var before = JsonSerializer.Serialize(store.Read(setup.Id));
        foreach (var rows in new[]
        {
            "fast,p,changed,3,10,offset,uint16,,,u,true\nslow,new,bad,3,70000,offset,uint16,,,u,true\n",
            "fast,p,changed,3,10,offset,uint16,,,u,true\nmissing,new,bad,3,0,offset,uint16,,,u,true\n",
        })
        {
            using var invalid = await admin.PostJsonAsync(Endpoint(setup.Id), Request(setup.Version, "Overwrite", rows), Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True((await invalid.ReadJsonAsync(Token)).GetProperty("errors").TryGetProperty("csv[3]", out _));
            Assert.Equal(before, JsonSerializer.Serialize(store.Read(setup.Id)));
        }
        using var stale = await admin.PostJsonAsync(Endpoint(setup.Id), Request("0", "Overwrite", "fast,p,x,3,1,offset,uint16,,,u,true\n"), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            "CREATE TRIGGER fail_csv BEFORE INSERT ON audit_log WHEN NEW.action = 'input.pointsImported' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        using var failed = await admin.PostJsonAsync(Endpoint(setup.Id), Request(setup.Version, "Overwrite", "slow,p,x,3,1,offset,uint16,,,u,true\n"), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(store.Read(setup.Id)));
        Assert.Empty(TestAccounts.Audit(factory.Services, "input.pointsImported"));
    }

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task ExportTemplateAndImportRequireEditorAndWritesRequireCsrf(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var setup = Seed(factory);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(Endpoint(setup.Id) + "?expectedVersion=" + setup.Version, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var deniedWrite = await anonymous.PostJsonAsync(Endpoint(setup.Id), Request(setup.Version, "Incremental", ""), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedWrite.StatusCode);
        TestAccounts.Create(factory.Services, "user", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("user", Token);
        using var exported = await client.GetAsync(Endpoint(setup.Id) + "?expectedVersion=" + setup.Version, Token);
        Assert.Equal(expected, exported.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("text/csv", exported.Content.Headers.ContentType!.MediaType);
            Assert.Equal(2, PointCsv.Parse(await exported.Content.ReadAsByteArrayAsync(Token), PointCsvEncoding.Utf8).Length);
        }
        using var template = await client.GetAsync(Endpoint(setup.Id) + "?template=true&expectedVersion=" + setup.Version, Token);
        Assert.Equal(expected, template.StatusCode);
        if (expected == HttpStatusCode.OK)
        { Assert.False(Assert.Single(PointCsv.Parse(await template.Content.ReadAsByteArrayAsync(Token), PointCsvEncoding.Utf8)).Point.Enabled); }
        using var imported = await client.PostJsonAsync(Endpoint(setup.Id), Request(setup.Version, "Incremental", "fast,p,x,3,1,offset,uint16,,,u,true\n"), Token);
        Assert.Equal(expected, imported.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint(setup.Id), Request(setup.Version, "Incremental", ""), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task ConcurrentImportsUseSingleGlobalVersionAndMissingOrWrongProtocolCannotImport()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var setup = Seed(factory);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var body = Request(setup.Version, "Overwrite", "fast,new,x,3,1,offset,uint16,,,u,true\n");
        var responses = await Task.WhenAll(client.PostJsonAsync(Endpoint(setup.Id), body, Token), client.PostJsonAsync(Endpoint(setup.Id), body, Token));
        using var first = responses[0];
        using var second = responses[1];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var version = factory.Services.GetRequiredService<InputDraftStore>().Read(setup.Id).Version;
        using var missing = await client.PostJsonAsync(Endpoint(1), body with { ExpectedVersion = version }, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var simulated = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new(version, "sim", "Sim", DeviceKind.Simulator), "fixture", null);
        using var wrong = await client.PostJsonAsync(Endpoint(simulated.Device.Id), body with { ExpectedVersion = simulated.Version }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task FinalGroupLimitRejectsWholeBatchAndExactTenThousandPointsCanBeExported()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var setup = Seed(factory);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var rows = Enumerable.Range(0, 10000).Select(index => $"fast,n{index},x,3,{index},offset,uint16,,,u,true\n").ToArray();
        var invalid = Assert.Throws<PointCsvException>(() => store.ImportPoints(setup.Id,
            Request(setup.Version, "Overwrite", string.Concat(rows)), "fixture", null));
        Assert.Contains(invalid.Issues, issue => issue.Message.Contains("10000", StringComparison.Ordinal));
        Assert.Equal(setup.Version, store.Read(setup.Id).Version);
        Assert.Equal(2, store.Read(setup.Id).Inputs.Sum(input => input.Modbus!.Points.Length));
        var saved = store.ImportPoints(setup.Id, Request(setup.Version, "Overwrite", string.Concat(rows.Take(9998))), "fixture", null);
        var exported = store.ExportPoints(setup.Id, saved.Version, false);
        Assert.Equal(10000, PointCsv.Parse(exported, PointCsvEncoding.Utf8).Length);
        var overwritten = store.ImportPoints(setup.Id, new(saved.Version, PointCsvMode.Overwrite, PointCsvEncoding.Utf8, exported), "fixture", null);
        Assert.Equal(10000, overwritten.Updated);
        Assert.Equal(0, overwritten.Added);
        Assert.Equal(10000, store.Read(setup.Id).Inputs.Sum(input => input.Modbus!.Points.Length));
    }

    private static PointCsvImportRequest Request(string version, string mode, string rows) =>
        new(version, Enum.Parse<PointCsvMode>(mode), PointCsvEncoding.Utf8, Encoding.UTF8.GetBytes(Header + rows));

    private static (long Id, string Version) Seed(TenonEdgeApplicationFactory factory)
    {
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "plc", "PLC", ConnectionKind.Modbus, Modbus: new("10.0.0.1")), "fixture", null);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Modbus, ConnectionId: connection.Connection.Id, UnitId: 1), "fixture", null);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var first = store.Write(device.Device.Id, null, new(device.Version, "fast", "Fast", Modbus: new(1000,
            [new ModbusPointSettings { Key = "p", FunctionCode = 3, Address = 0, Type = "uint16" },
             new ModbusPointSettings { Key = "keep", FunctionCode = 3, Address = 2, Type = "uint16" }])), "fixture", null);
        var second = store.Write(device.Device.Id, null, new(first.Version, "slow", "Slow", Modbus: new(2000, [])), "fixture", null);
        return (device.Device.Id, second.Version);
    }
}

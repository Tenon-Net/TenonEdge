using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class InputModuleReferenceDraftTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static FixedDataDirectoryApplicationFactory Host(string path, Module? module = null) => new(path,
        configureServices: services => services.AddSingleton<IInputModuleFactory>(module ?? new Module()));
    private static ConnectionDraftResult Connection(IServiceProvider services, string version, string key, bool enabled = true) =>
        services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(version, key, key, ConnectionKind.Http, enabled, Http: new("http://localhost/")), "test", null);
    private static DeviceDraftResult Device(IServiceProvider services, string version) => services.GetRequiredService<DeviceDraftStore>().Write(null,
        new(version, "meter", "Meter", DeviceKind.Module, ModuleKind: "input.testrefs"), "test", null);
    private static InputDraftRequest Request(string version, long primary, long backup) => new(version, "source", "Source",
        Module: new("input.testrefs", 1, Json("{\"value\":9007199254740993}"), new() { ["primary"] = primary, ["backup"] = backup }));
    private static RouteGraph Graph(long source, long output) => new(1,
        [new("in", "input.testrefs", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { source } })),
            new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = output }))],
        [new("in", "out")], new([]));

    [Theory]
    [InlineData("administrator")]
    [InlineData("configurator")]
    [InlineData("viewer")]
    public async Task ReferenceWritesKeepRoleAndCsrfBoundaries(string roleName)
    {
        var role = Enum.Parse<UserRole>(roleName, ignoreCase: true);
        using var data = TemporaryDirectory.Create();
        var module = new Module();
        await using var host = Host(data.Path, module);
        var primary = Connection(host.Services, "0", "primary");
        var backup = Connection(host.Services, primary.Version, "backup");
        var device = Device(host.Services, backup.Version);
        var path = $"/api/edge-admin/v1/devices/{device.Device.Id}/inputs";
        var request = Request(device.Version, primary.Connection.Id, backup.Connection.Id);
        using var anonymous = host.CreateClient();
        using var deniedAnonymous = await anonymous.PostAsJsonAsync(path, request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedAnonymous.StatusCode);
        TestAccounts.Create(host.Services, "reference-user", ApiRequests.StrongPassword, role);
        using var client = await host.CreateLoggedInClientAsync("reference-user", Token);
        var csrf = client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var deniedCsrf = await client.PostAsJsonAsync(path, request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, deniedCsrf.StatusCode);
        Assert.Equal(0, module.Descriptions);
        Assert.Empty(host.Services.GetRequiredService<InputDraftStore>().Read(device.Device.Id).Inputs);
        client.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        using var written = await client.PostAsJsonAsync(path, request, Token);
        Assert.Equal(role == UserRole.Viewer ? HttpStatusCode.Forbidden : HttpStatusCode.OK, written.StatusCode);
        if (role == UserRole.Viewer)
        {
            Assert.Equal(0, module.Descriptions);
            Assert.Empty(host.Services.GetRequiredService<InputDraftStore>().Read(device.Device.Id).Inputs);
        }
        else
        {
            var result = (await written.Content.ReadFromJsonAsync<InputDraftResult>(RouteSourceCompiler.Json, Token))!;
            Assert.Equal(primary.Connection.Id, result.Input.Module!.Connections!["primary"]);
            Assert.Equal("9007199254740993", result.Input.Module.Parameters.GetProperty("value").GetRawText());
            using var listed = await client.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Contains("9007199254740993", await listed.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SameVersionReferencesCompilePreviewCopyAndExportAfterModuleUninstall()
    {
        using var data = TemporaryDirectory.Create();
        byte[] exported;
        long primaryId;
        long backupId;
        await using (var host = Host(data.Path))
        {
            var primary = Connection(host.Services, "0", "primary");
            var backup = Connection(host.Services, primary.Version, "backup");
            var output = Connection(host.Services, backup.Version, "sink");
            primaryId = primary.Connection.Id;
            backupId = backup.Connection.Id;
            var device = Device(host.Services, output.Version);
            var inputs = host.Services.GetRequiredService<InputDraftStore>();
            var input = inputs.Write(device.Device.Id, null, Request(device.Version, primaryId, backupId), "test", null);
            Assert.Equal("9007199254740993", input.Input.Module!.Parameters.GetProperty("value").GetRawText());
            Assert.False(input.Input.Module.Parameters.TryGetProperty("primary", out _));
            var connections = host.Services.GetRequiredService<ConnectionDraftStore>();
            foreach (var id in new[] { primaryId, backupId })
            { Assert.Equal("connection.deviceReferenced", Assert.Throws<DraftWriteException>(() => connections.Delete(id, input.Version, "test", null)).Code); }
            var routes = host.Services.GetRequiredService<RouteDraftStore>();
            var graph = Graph(input.Input.Id, output.Connection.Id);
            var snapshot = routes.ReadGraphCompilation(input.Version, graph, Token);
            Assert.Equal(3, snapshot.Connections.Count); // Includes module references, not only the output connection.
            var compiled = RoutePlanCompiler.Compile(snapshot, Token);
            var frozen = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind == "input.testrefs");
            Assert.Equal(primaryId, InputModuleCatalog.ReadConnections(frozen)["primary"].ConnectionId);
            Assert.Equal(backupId, InputModuleCatalog.ReadConnections(frozen)["backup"].ConnectionId);
            var sample = RoutePreviewSample.Decode(snapshot, input.Input.Id, "{\"value\":9007199254740993}", DateTimeOffset.UtcNow, ClockSyncState.Unknown, Token);
            Assert.Contains("9007199254740993", Assert.Single(RouteSamplePreview.Run(snapshot, input.Input.Id, sample.Message, Token).Paths).Output!.Body, StringComparison.Ordinal);
            Assert.NotNull(RouteFieldAnalysis.Analyze(snapshot, "out", null, null, 100, Token));
            var copied = host.Services.GetRequiredService<DeviceDraftStore>().Copy(device.Device.Id, new(input.Version, 1), "test", null);
            var clone = Assert.Single(inputs.Read(Assert.Single(copied.Devices).Id).Inputs);
            Assert.Equal(primaryId, clone.Module!.Connections!["primary"]);
            Assert.Equal(backupId, clone.Module.Connections["backup"]);
            exported = host.Services.GetRequiredService<ConfigurationExportStore>().Export(copied.Version, Token);
        }
        await using var missing = new FixedDataDirectoryApplicationFactory(data.Path);
        Assert.Equal(exported, missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        var portable = ConfigurationImportDocument.Read(exported, Token);
        Assert.All(portable.Inputs, input =>
        {
            Assert.Equal("primary", input.Module!.Connections!["primary"]);
            Assert.Equal("backup", input.Module.Connections["backup"]);
            Assert.Equal("9007199254740993", input.Module.Parameters.GetProperty("value").GetRawText());
        });
        var text = System.Text.Encoding.UTF8.GetString(exported);
        Assert.DoesNotContain(primaryId.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.DoesNotContain(backupId.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        var version = missing.Services.GetRequiredService<ConnectionDraftStore>().Read().Version;
        Assert.Equal("connection.deviceReferenced", Assert.Throws<DraftWriteException>(() =>
            missing.Services.GetRequiredService<ConnectionDraftStore>().Delete(primaryId, version, "test", null)).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("protocol")]
    [InlineData("undeclared")]
    [InlineData("duplicate")]
    [InlineData("unbound")]
    [InlineData("range")]
    public async Task InvalidBindingsAreRejectedWithoutWritingOrExecutingDescribe(string reason)
    {
        using var data = TemporaryDirectory.Create();
        var module = new Module();
        await using var host = Host(data.Path, module);
        var first = Connection(host.Services, "0", "primary");
        var second = reason == "protocol"
            ? host.Services.GetRequiredService<ConnectionDraftStore>().Write(null, new(first.Version, "backup", "Backup", ConnectionKind.Mqtt, Mqtt: new("localhost")), "test", null)
            : Connection(host.Services, first.Version, "backup");
        var device = Device(host.Services, second.Version);
        var request = Request(device.Version, first.Connection.Id, second.Connection.Id);
        var refs = request.Module!.Connections!;
        switch (reason)
        {
            case "missing":
                refs["backup"] = 42;
                break;
            case "undeclared":
                refs["unknown"] = first.Connection.Id;
                break;
            case "duplicate":
                request = request with { Module = request.Module with { Parameters = Json("{\"primary\":\"42\",\"value\":1}") } };
                break;
            case "unbound":
                request = request with { Module = request.Module with { Connections = null, Parameters = Json("{\"primary\":\"42\",\"value\":1}") } };
                break;
            case "range":
                refs["backup"] = 9007199254740992;
                break;
        }
        var store = host.Services.GetRequiredService<InputDraftStore>();
        Assert.Throws<ArgumentException>(() => store.Write(device.Device.Id, null, request, "test", null));
        Assert.Empty(store.Read(device.Device.Id).Inputs);
        Assert.Equal(device.Version, store.Read(device.Device.Id).Version);
        Assert.Equal(0, module.Descriptions);
    }

    [Fact]
    public async Task DisabledReferenceCanBeSavedButCannotCompileAndConnectionChangeDuringDescribeConflicts()
    {
        using var data = TemporaryDirectory.Create();
        var module = new Module();
        await using var host = Host(data.Path, module);
        var primary = Connection(host.Services, "0", "primary", enabled: false);
        var backup = Connection(host.Services, primary.Version, "backup");
        var device = Device(host.Services, backup.Version);
        var inputs = host.Services.GetRequiredService<InputDraftStore>();
        var saved = inputs.Write(device.Device.Id, null, Request(device.Version, primary.Connection.Id, backup.Connection.Id), "test", null);
        var graph = Graph(saved.Input.Id, backup.Connection.Id);
        var snapshot = host.Services.GetRequiredService<RouteDraftStore>().ReadGraphCompilation(saved.Version, graph, Token);
        Assert.Equal("source.disabled", Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(snapshot, Token)).Issue.Code);
        module.OnDescribe = () => Task.Run(() => host.Services.GetRequiredService<ConnectionDraftStore>().Write(primary.Connection.Id,
            new(saved.Version, "primary", "Concurrent change", ConnectionKind.Http, Http: new("http://changed.invalid/")), "other", null), Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() =>
            inputs.Write(device.Device.Id, saved.Input.Id, Request(saved.Version, primary.Connection.Id, backup.Connection.Id), "test", null)).Code);
        Assert.Equal("Source", Assert.Single(inputs.Read(device.Device.Id).Inputs).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableReferencesRemapThroughLocalBusinessKeysAndCommitOnlyDraft(bool replace)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var existing = Connection(host.Services, "0", "primary");
        var file = new PortableConfiguration(1,
            [new("backup", "Backup", ConnectionKind.Http, true, null, null, new("http://localhost/"), null),
                new("primary", "Primary", ConnectionKind.Http, true, null, null, new("http://localhost/"), null)],
            [new("meter", "Meter", DeviceKind.Module, null, null, null, ModbusAddressNotation.Offset, true, "input.testrefs")],
            [new("meter", "source", "Source", DeviceKind.Module, true, null, null, null, null,
                new("input.testrefs", 1, Json("{\"value\":9007199254740993}"), new() { ["primary"] = "primary", ["backup"] = "backup" }))], []);
        var store = host.Services.GetRequiredService<ConfigurationImportStore>();
        var bytes = ConfigurationExport.Serialize(file, Token);
        var candidate = store.Prepare(bytes, replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge, existing.Version, Token);
        var input = Assert.Single(candidate.Draft.Inputs);
        Assert.Equal(existing.Connection.Id, input.Module!.Connections!["primary"]);
        var backup = Assert.Single(candidate.Draft.Connections, value => value.Key == "backup");
        Assert.Equal(backup.Id, input.Module.Connections["backup"]);
        TestAccounts.Create(host.Services, "importer", ApiRequests.StrongPassword, UserRole.Configurator);
        var actor = TestAccounts.Find(host.Services, "importer")!;
        var committed = store.Commit(candidate, new(actor.Id, actor.SecurityStamp, null), Token);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        var snapshot = host.Services.GetRequiredService<RouteDraftStore>().ReadGraphCompilation(committed.Version, Graph(input.Id, backup.Id), Token);
        Assert.NotNull(RoutePlanCompiler.Compile(snapshot, Token));
        file.Inputs[0].Module!.Connections!["backup"] = "missing";
        Assert.Throws<ConfigurationImportException>(() => store.Prepare(ConfigurationExport.Serialize(file, Token), ConfigurationImportMode.Replace, committed.Version, Token));
        Assert.Equal(committed.Version, host.Services.GetRequiredService<InputDraftStore>().Read(input.DeviceId).Version);
    }

    private sealed class Module : IInputModuleFactory
    {
        public string Kind => "input.testrefs";
        public int Version => 1;
        public int Descriptions { get; private set; }
        public Action? OnDescribe { get; set; }
        public ParameterSchema Parameters { get; } = new("input.testrefs", ParameterScope.Input,
            [new("primary", "主要连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] },
                new("backup", "备用连接", ParameterType.ConnectionReference) { ConnectionKinds = ["http"] },
                new("value", "数值", ParameterType.Number) { Required = true, WholeNumber = true }]);
        public ImmutableArray<InputModuleField> Describe(JsonElement parameters)
        {
            Descriptions++;
            OnDescribe?.Invoke();
            return [new("value", FieldType.Integer, "count")];
        }
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => throw new InvalidOperationException("Draft operations must not build inputs.");
    }
}

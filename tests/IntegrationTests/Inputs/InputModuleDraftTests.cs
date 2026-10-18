using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class InputModuleDraftTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static InputDraftRequest Request(string version, string parameters = "{\"value\":9007199254740993}", int moduleVersion = 3) =>
        new(version, "source", "Source", Module: new("input.testscalar", moduleVersion, Json(parameters)));
    internal static DeviceDraftResult Device(IServiceProvider services, string version = "0") => services.GetRequiredService<DeviceDraftStore>()
        .Write(null, new(version, "meter", "Meter", DeviceKind.Module, ModuleKind: "input.testscalar"), "test", null);
    internal static RouteGraph Graph(long sourceId, long connectionId, int version = 3) => new(1,
        [new("in", "input.testscalar", version, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { sourceId } })),
            new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId }))],
        [new("in", "out")], new([]));
    private static FixedDataDirectoryApplicationFactory Host(string path, TestScalarInputModule? module = null) =>
        new(path, configureServices: services => services.AddSingleton<IInputModuleFactory>(module ?? new TestScalarInputModule()));

    [Theory]
    [InlineData("administrator")]
    [InlineData("configurator")]
    [InlineData("viewer")]
    public async Task DiscoveryUsesTheInstalledDirectoryAndDraftWritesKeepExistingPermissions(string roleName)
    {
        using var data = TemporaryDirectory.Create();
        var role = Enum.Parse<UserRole>(roleName, ignoreCase: true);
        var module = new TestScalarInputModule();
        await using var host = Host(data.Path, module);
        var device = Device(host.Services);
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/edge-admin/v1/input-modules", Token)).StatusCode);
        TestAccounts.Create(host.Services, "module-user", ApiRequests.StrongPassword, role);
        using var client = await host.CreateLoggedInClientAsync("module-user", Token);
        using var metadata = await client.GetAsync("/api/edge-admin/v1/input-modules", Token);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.True(metadata.Headers.CacheControl!.NoStore);
        using var document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(Token));
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(module.Kind, entry.GetProperty("kind").GetString());
        Assert.Equal(3, entry.GetProperty("version").GetInt32());
        Assert.Equal("number", entry.GetProperty("parameters").GetProperty("parameters")[0].GetProperty("type").GetString());
        using var written = await client.PostAsJsonAsync($"/api/edge-admin/v1/devices/{device.Device.Id}/inputs", Request(device.Version), Token);
        Assert.Equal(role == UserRole.Viewer ? HttpStatusCode.Forbidden : HttpStatusCode.OK, written.StatusCode);
        Assert.Equal(0, module.Builds);
        Assert.Equal(0, module.Starts);
        if (role != UserRole.Viewer)
        {
            Assert.Contains("9007199254740993", await written.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
            var list = host.Services.GetRequiredService<RouteDraftStore>().ReadCatalog(module.Kind, 0, 100, 3);
            Assert.Single(list.Items);
            Assert.Throws<ArgumentException>(() => host.Services.GetRequiredService<RouteDraftStore>().ReadCatalog(module.Kind, 0, 100, 2));
        }
    }

    [Fact]
    public async Task CookieWriteStillRequiresCsrfAndMissingDeviceModuleCannotBeSaved()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var device = Device(host.Services);
        TestAccounts.Create(host.Services, "module-admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await host.CreateLoggedInClientAsync("module-admin", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var denied = await client.PostAsJsonAsync($"/api/edge-admin/v1/devices/{device.Device.Id}/inputs", Request(device.Version), Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Throws<ArgumentException>(() => host.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(device.Version, "missing", "Missing", DeviceKind.Module, ModuleKind: "input.missing"), "test", null));
        Assert.Empty(host.Services.GetRequiredService<InputDraftStore>().Read(device.Device.Id).Inputs);
    }

    [Theory]
    [InlineData("{}", 3)]
    [InlineData("{\"value\":-1}", 3)]
    [InlineData("{\"value\":1,\"password\":\"fixture-secret\"}", 3)]
    [InlineData("{\"value\":1,\"value\":2}", 3)]
    [InlineData("{\"value\":{\"nested\":1}}", 3)]
    [InlineData("{\"value\":1}", 99)]
    public async Task UnavailableVersionOrInvalidParametersDoNotChangeDraftsOrLeakSecrets(string parameters, int version)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var device = Device(host.Services);
        var store = host.Services.GetRequiredService<InputDraftStore>();
        var error = Assert.Throws<ArgumentException>(() => store.Write(device.Device.Id, null, Request(device.Version, parameters, version), "test", null));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(device.Version, store.Read(device.Device.Id).Version);
        Assert.Empty(store.Read(device.Device.Id).Inputs);
    }

    [Fact]
    public async Task WrongOwnerAndMissingSourceAreRejectedBeforeDescription()
    {
        using var data = TemporaryDirectory.Create();
        var descriptions = 0;
        var module = new TestScalarInputModule { OnDescribe = () => Interlocked.Increment(ref descriptions) };
        await using var host = Host(data.Path, module);
        var device = Device(host.Services);
        var store = host.Services.GetRequiredService<InputDraftStore>();
        var source = store.Write(device.Device.Id, null, Request(device.Version), "test", null);
        var other = host.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(source.Version, "other", "Other", DeviceKind.Module, ModuleKind: module.Kind), "test", null);
        descriptions = 0;
        foreach (var id in new[] { source.Input.Id, long.MaxValue })
        {
            Assert.Equal("input.notFound", Assert.Throws<DraftWriteException>(() =>
                store.Write(other.Device.Id, id, Request(other.Version), "test", null)).Code);
        }
        Assert.Equal(0, descriptions);
        Assert.Equal(other.Version, store.Read(other.Device.Id).Version);
        Assert.Empty(store.Read(other.Device.Id).Inputs);
    }

    [Fact]
    public async Task DescriptionRunsOutsideTransactionsAndConcurrentDraftChangeRejectsTheWrite()
    {
        using var data = TemporaryDirectory.Create();
        IServiceProvider? services = null;
        DeviceDraftResult? device = null;
        var changed = 0;
        var module = new TestScalarInputModule
        {
            OnDescribe = () =>
        {
            if (Interlocked.Exchange(ref changed, 1) != 0)
            { return; }
            Task.Run(() => services!.GetRequiredService<DeviceDraftStore>().Write(device!.Device.Id,
                new(device.Version, "meter", "Concurrent", DeviceKind.Module, ModuleKind: "input.testscalar"), "other", null), Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token).GetAwaiter().GetResult();
        }
        };
        await using var host = Host(data.Path, module);
        services = host.Services;
        device = Device(services);
        var store = services.GetRequiredService<InputDraftStore>();
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() =>
            store.Write(device.Device.Id, null, Request(device.Version), "test", null)).Code);
        Assert.Empty(store.Read(device.Device.Id).Inputs);
        Assert.Equal("Concurrent", Assert.Single(services.GetRequiredService<DeviceDraftStore>().Read().Devices).Name);
        Assert.Equal(0, module.Builds);
    }

    [Fact]
    public async Task CopyAndPortableRoundTripPreserveExactParametersWhileMissingModuleImportRejects()
    {
        using var data = TemporaryDirectory.Create();
        byte[] file;
        await using (var host = Host(data.Path))
        {
            var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new("0", "sink", "Sink", ConnectionKind.Http, Http: new("http://example.com/")), "test", null);
            var device = Device(host.Services, connection.Version);
            var store = host.Services.GetRequiredService<InputDraftStore>();
            var input = store.Write(device.Device.Id, null, Request(device.Version), "test", null);
            Assert.Equal("input.duplicateDeviceField", Assert.Throws<DraftWriteException>(() => store.Write(device.Device.Id, null,
                Request(input.Version) with { Key = "second" }, "test", null)).Code);
            var copied = host.Services.GetRequiredService<DeviceDraftStore>().Copy(device.Device.Id, new(input.Version, 1), "test", null);
            var copy = Assert.Single(copied.Devices);
            Assert.False(copy.Enabled);
            Assert.Equal("input.testscalar", copy.ModuleKind);
            Assert.Equal("9007199254740993", Assert.Single(store.Read(copy.Id).Inputs).Module!.Parameters.GetProperty("value").GetRawText());
            var route = host.Services.GetRequiredService<RouteDraftStore>().Write(null,
                new(copied.Version, "module_route", "Module route", true, Graph(input.Input.Id, connection.Connection.Id)), "test", null);
            file = host.Services.GetRequiredService<ConfigurationExportStore>().Export(route.Version, Token);
        }
        await using (var uninstalled = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            // Reading/exporting retained drafts cannot execute an uninstalled module.
            Assert.Equal(file, uninstalled.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
            Assert.Throws<ConfigurationImportException>(() => uninstalled.Services.GetRequiredService<ConfigurationImportStore>()
                .Prepare(file, ConfigurationImportMode.Replace, uninstalled.Services.GetRequiredService<DeviceDraftStore>().Read().Version, Token));
        }
        using var other = TemporaryDirectory.Create();
        await using var installed = Host(other.Path);
        var candidate = installed.Services.GetRequiredService<ConfigurationImportStore>().Prepare(file, ConfigurationImportMode.Replace, "0", Token);
        Assert.Equal(2, candidate.Draft.Inputs.Length);
        Assert.All(candidate.Draft.Inputs, input => Assert.Equal("9007199254740993", input.Module!.Parameters.GetProperty("value").GetRawText()));
        Assert.Null(installed.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        TestAccounts.Create(installed.Services, "module-importer", ApiRequests.StrongPassword, UserRole.Configurator);
        var actor = TestAccounts.Find(installed.Services, "module-importer")!;
        var committed = installed.Services.GetRequiredService<ConfigurationImportStore>().Commit(candidate,
            new(actor.Id, actor.SecurityStamp, "127.0.0.1"), Token);
        var snapshot = installed.Services.GetRequiredService<RouteDraftStore>().ReadCompilation(committed.Version, Token);
        Assert.Single(RoutePlanCompiler.Compile(snapshot, Token).Document.Units, unit => unit.Id.Kind == "input.testscalar");
        Assert.Null(installed.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }

    [Fact]
    public async Task SourceVersionMustMatchTheGraphEvenWhenBothVersionsAreInstalled()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule());
            services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { Version = 2 });
        });
        var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "sink", "Sink", ConnectionKind.Http, Http: new("http://example.com/")), "test", null);
        var device = Device(host.Services, connection.Version);
        var input = host.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null, Request(device.Version), "test", null);
        var routes = host.Services.GetRequiredService<RouteDraftStore>();
        var graph = Graph(input.Input.Id, connection.Connection.Id, 2);
        var captured = routes.ReadGraphCompilation(input.Version, graph, Token);
        Assert.Equal("source.kind", Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(captured, Token)).Issue.Code);
        Assert.Equal("source.kind", Assert.Throws<RouteTopologyException>(() => RouteFieldAnalysis.Analyze(captured, "out", null, null, 100, Token)).Issue.Code);
        Assert.Empty(routes.ReadCatalog("input.testscalar", 0, 100, 2).Items);
        Assert.Single(routes.ReadCatalog("input.testscalar", 0, 100, 3).Items);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }
}

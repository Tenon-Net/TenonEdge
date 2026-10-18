using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class InputModuleProtocolDraftTests
{
    [Theory]
    [InlineData("mqtt", false)]
    [InlineData("mqtt", true)]
    [InlineData("modbus", false)]
    [InlineData("modbus", true)]
    public async Task ProtocolReferencesImportAndExportBusinessKeysWithoutStartingOrLosingDeletionProtection(string protocol, bool replace)
    {
        var token = TestContext.Current.CancellationToken;
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
            services.AddSingleton<IInputModuleFactory>(new TestProtocolInputModule(protocol)));
        var connections = host.Services.GetRequiredService<ConnectionDraftStore>();
        var kind = protocol == "mqtt" ? ConnectionKind.Mqtt : ConnectionKind.Modbus;
        var existing = connections.Write(null, new("0", "source", "Source", kind,
            Mqtt: kind == ConnectionKind.Mqtt ? new("localhost") : null,
            Modbus: kind == ConnectionKind.Modbus ? new("localhost") : null), "test", null);
        var parameters = JsonSerializer.Deserialize<JsonElement>(protocol == "mqtt" ? "{\"topic\":\"in/value\"}" : "{\"unit\":1}");
        var file = new PortableConfiguration(1,
            [new("source", "Source", kind, true, existing.Connection.Modbus, existing.Connection.Mqtt, null, null)],
            [new("meter", "Meter", DeviceKind.Module, null, null, null, ModbusAddressNotation.Offset, true, "input.test" + protocol)],
            [new("meter", "source", "Source", DeviceKind.Module, true, null, null, null, null,
                new("input.test" + protocol, 1, parameters, new() { ["connection"] = "source" }))], []);
        var store = host.Services.GetRequiredService<ConfigurationImportStore>();
        var candidate = store.Prepare(ConfigurationExport.Serialize(file, token), replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge, existing.Version, token);
        var source = Assert.Single(candidate.Draft.Inputs);
        Assert.Equal(existing.Connection.Id, source.Module!.Connections!["connection"]);
        TestAccounts.Create(host.Services, "importer", ApiRequests.StrongPassword, UserRole.Configurator);
        var actor = TestAccounts.Find(host.Services, "importer")!;
        var committed = store.Commit(candidate, new(actor.Id, actor.SecurityStamp, null), token);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        var frozen = host.Services.GetRequiredService<InputModuleCatalog>().WithFrozenFields(source.Module.ToUnit("meter", "source",
            new Dictionary<long, ConnectionDraft> { [existing.Connection.Id] = existing.Connection }));
        Assert.Equal("input." + protocol, InputModuleCatalog.ReadProtocol(frozen)!.Id.Kind);
        var exported = ConfigurationImportDocument.Read(host.Services.GetRequiredService<ConfigurationExportStore>().Export(committed.Version, token), token);
        Assert.Equal("source", Assert.Single(exported.Inputs).Module!.Connections!["connection"]);
        Assert.Equal(parameters.GetRawText(), Assert.Single(exported.Inputs).Module!.Parameters.GetRawText());
        Assert.Equal("connection.deviceReferenced", Assert.Throws<DraftWriteException>(() => connections.Delete(existing.Connection.Id, committed.Version, "test", null)).Code);
        file.Inputs[0].Module!.Connections!["connection"] = "missing";
        Assert.Throws<ConfigurationImportException>(() => store.Prepare(ConfigurationExport.Serialize(file, token), ConfigurationImportMode.Merge, committed.Version, token));
        Assert.Equal(committed.Version, connections.Read().Version);
    }
}

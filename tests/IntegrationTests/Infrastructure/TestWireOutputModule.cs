using System.Globalization;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试侧新传输声明；不是 HTTP/MQTT 委托或产品内置协议。</summary>
internal sealed class TestWireOutputModule : IOutputModuleFactory
{
    private static readonly string[] Sources = ["meter/source"];
    public static ConnectionModuleDefinition Definition { get; } = new("wire", 1, "Wire", new("connection.wire", ParameterScope.Connection,
        [new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 253 },
         new("port", "Port", ParameterType.Number) { Required = true, MinimumJson = "1", MaximumJson = "65535", WholeNumber = true }]), ["host", "port"]);
    public string Kind => "output.wiremodule";
    public int Version => 1;
    public int Calls { get; private set; }
    public bool Throw { get; set; }
    public ParameterSchema Parameters { get; } = new("output.wiremodule", ParameterScope.Output,
        [new("connection", "Connection", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["wire"] },
         new("address", "Address", ParameterType.Text) { Required = true, MaximumLength = 1024 }]);
    public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
    {
        Calls++;
        if (Throw)
        { throw new InvalidOperationException("fixture-secret"); }
        var connection = context.Connections["connection"];
        var settings = ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters;
        return new("output.wire", JsonSerializer.SerializeToElement(new
        {
            connectionId = context.ConnectionId,
            inputs = context.Inputs,
            connection,
            transportRoute = new
            {
                recipient = $"wire://{settings.GetProperty("host").GetString()}:{settings.GetProperty("port").GetInt32()}",
                addressTemplate = parameters.GetProperty("address").GetString(),
            },
        }, JsonSerializerOptions.Web));
    }
    internal RuntimeUnitPlan Unit(int port = 19001, string address = "events/{deviceKey}", long id = 1, string key = "route/out", string host = "127.0.0.1")
    {
        var connections = new ConnectionModuleCatalog([Definition]);
        var connection = connections.Freeze(id, "wire", 1, JsonSerializer.SerializeToElement(new { host, port }));
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create(Kind, key), JsonSerializer.SerializeToElement(new
        {
            connectionId = id,
            inputs = Sources,
            outputModule = new
            {
                version = 1,
                parameters = new { connection = id.ToString(CultureInfo.InvariantCulture), address },
                connections = new { connection },
            },
        }, JsonSerializerOptions.Web));
        return new OutputModuleCatalog([this], connections).WithFrozenProtocol(unit);
    }
}

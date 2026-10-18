using System.Globalization;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试侧纯输出声明，所有报文生成、发送、连接与重试均由原协议和运行时负责。</summary>
internal sealed class TestProtocolOutputModule(string protocol) : IOutputModuleFactory
{
    public string Kind => "output.test" + protocol;
    public int Version { get; init; } = 1;
    public string DisplayName { get; init; } = "output.test" + protocol;
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public ParameterSchema Parameters { get; init; } = new("output.test" + protocol, ParameterScope.Output,
        [new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [protocol] },
            new("address", "路径/主题", ParameterType.Text) { Required = true, MaximumLength = 1024 }]);
    public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
    {
        Calls++;
        if (Throw)
        { throw new InvalidOperationException("fixture-secret"); }
        var connection = context.Connections["connection"];
        var address = parameters.GetProperty("address").GetString()!;
        var json = TestProtocolInputModule.Json;
        if (protocol == "http")
        {
            var settings = connection.Configuration.Deserialize<HttpOutputSettings>(json)! with
            { ConnectionId = connection.ConnectionId, Inputs = context.Inputs, Path = address };
            return new("output.http", JsonSerializer.SerializeToElement(settings, json));
        }
        var mqtt = connection.Configuration.Deserialize<MqttOutputSettings>(json)! with
        { ConnectionId = connection.ConnectionId, Inputs = context.Inputs.ToArray(), Topic = address, Qos = 1 };
        return new("output.mqtt", JsonSerializer.SerializeToElement(mqtt, json));
    }

    internal RuntimeUnitPlan Unit(JsonElement connection, string address = "send", long id = 1, string key = "route/out", params string[] inputs)
    {
        var plan = new RuntimeUnitPlan(RuntimeUnitId.Create(Kind, key), JsonSerializer.SerializeToElement(new
        {
            connectionId = id,
            inputs = inputs.Length == 0 ? ["meter/source"] : inputs,
            outputModule = new
            {
                version = Version,
                parameters = new { connection = id.ToString(CultureInfo.InvariantCulture), address },
                connections = new { connection = new { connectionId = id, kind = protocol, configuration = connection } },
            }
        }));
        return new OutputModuleCatalog([this]).WithFrozenProtocol(plan);
    }
    internal static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    { schemaVersion = 1, units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }) });
}

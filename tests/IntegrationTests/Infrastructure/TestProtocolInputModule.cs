using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试侧声明/纯委托：没有客户端、线程或第二套计划工厂。</summary>
internal sealed class TestProtocolInputModule(string protocol, bool referenceWrapper = false) : IInputModuleFactory
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    public string Kind => "input.test" + protocol;
    public int Version => 1;
    public bool UsesDurableHandoff => protocol == "mqtt";
    public ParameterSchema Parameters { get; } = new("input.test" + protocol, ParameterScope.Input,
        [new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [referenceWrapper ? "wrapper" : protocol] },
            protocol == "mqtt" ? new("topic", "主题", ParameterType.Text) { Required = true, MaximumLength = 65535 }
                : new("unit", "从站", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "0", MaximumJson = "255" }]);
    public ImmutableArray<InputModuleField> Describe(JsonElement parameters) => [new("value", FieldType.Integer, "count")];
    public InputProtocolBinding BindProtocol(JsonElement parameters, IReadOnlyDictionary<string, ModuleConnectionSnapshot> connections)
    {
        var connection = connections["connection"];
        if (referenceWrapper)
        { connection = ModuleConnectionReferences.Read(connection.Configuration.GetProperty("connectionModule"))["upstream"]; }
        if (protocol == "mqtt")
        {
            var settings = connection.Configuration.Deserialize<MqttInputSettings>(Json)! with
            {
                ConnectionId = connection.ConnectionId,
                TopicFilter = parameters.GetProperty("topic").GetString()!,
                Qos = 1,
                Fields = JsonSerializer.SerializeToElement(new[] { new { key = "value", path = "/value", type = "integer" } }),
            };
            return new("input.mqtt", JsonSerializer.SerializeToElement(settings, Json));
        }
        var transport = connection.Configuration.Deserialize<ModbusConnectionDraft>(Json)!;
        var modbus = new ModbusInputSettings
        {
            ConnectionId = connection.ConnectionId,
            Host = transport.Host,
            Port = transport.Port,
            FrameFormat = transport.Format == ModbusFrameFormat.Tcp ? "tcp" : "rtu-over-tcp",
            ConnectTimeoutMs = transport.ConnectTimeoutMs,
            ReadTimeoutMs = transport.ReadTimeoutMs,
            RequestIntervalMs = transport.RequestIntervalMs,
            RtuSilenceMs = transport.RtuSilenceMs,
            UnitId = parameters.GetProperty("unit").GetByte(),
            PeriodMs = 1000,
            Points = [new() { Key = "value", FunctionCode = 3, Address = 0, Type = "uint16" }],
        };
        return new("input.modbus", JsonSerializer.SerializeToElement(modbus, Json));
    }
    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) =>
        throw new InvalidOperationException("Protocol delegates must use the original plan factory, never the module Build.");
}

using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试侧嵌套连接声明，沿用原HTTP采集与共享连接；不注册到产品宿主。</summary>
internal sealed class TestNestedHttpModule(HttpInputFactory http) : IInputModuleFactory
{
    internal static ConnectionModuleDefinition Connection { get; } = new("nestedhttp", 1, "测试嵌套连接",
        new("connection.nestedhttp", ParameterScope.Connection,
        [
            new("label", "连接标签", ParameterType.Text) { Required = true, MaximumLength = 64 },
            new("precision", "精确参数", ParameterType.Number) { WholeNumber = true, DefaultJson = "9007199254740993" },
            new("flag", "测试开关", ParameterType.Switch) { DefaultJson = "true" },
            new("mode", "测试选项", ParameterType.Choice) { DefaultJson = "\"one\"", Options = [new("one", "选项一"), new("two", "选项二")] },
            new("upstream", "上游连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http", "nestedhttp"] },
            new("auth", "认证秘密", ParameterType.Credential),
        ]), ["label"]);

    public string Kind => "input.nestedhttp";
    public int Version => 1;
    public ParameterSchema Parameters { get; } = new("input.nestedhttp", ParameterScope.Input,
        [new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["nestedhttp"] }]);
    public ImmutableArray<InputModuleField> Describe(JsonElement parameters) => [new("value", FieldType.Integer, "count")];

    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
    {
        var connection = context.Connections["connection"];
        while (connection.Kind == "nestedhttp")
        { connection = ModuleConnectionReferences.Read(connection.Configuration.GetProperty("connectionModule"))["upstream"]; }
        if (connection.Kind != "http")
        { throw new ArgumentException("测试嵌套连接须终止于HTTP。"); }
        var settings = connection.Configuration.Deserialize<HttpConnectionDraft>(JsonSerializerOptions.Web)!;
        return http.Build(new(unit.Id, JsonSerializer.SerializeToElement(new
        {
            connectionId = connection.ConnectionId,
            baseAddress = settings.BaseAddress,
            timeoutMs = settings.TimeoutMs,
            headers = settings.Headers,
            path = "nested",
            periodMs = 1000,
            // Unit is frozen by Describe; the original decoder accepts only key/path/type.
            fields = new[] { new { key = "value", path = "/value", type = "integer" } },
        })), context);
    }
}

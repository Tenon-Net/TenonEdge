using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试侧认证帧输出，复用 authwire 连接凭证声明和原 TCP 帧传输；不注册到产品宿主。</summary>
internal sealed class TestAuthenticatedWireOutputModule : IOutputModuleFactory
{
    public string Kind => "output.authwiremodule";
    public int Version => 1;
    public ParameterSchema Parameters { get; } = new("output.authwiremodule", ParameterScope.Output,
        [new("connection", "Connection", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["authwire"] },
         new("address", "Address", ParameterType.Text) { Required = true, MaximumLength = 1024 }]);

    public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
    {
        var connection = context.Connections["connection"];
        var settings = ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters;
        return new("output.authwire", JsonSerializer.SerializeToElement(new
        {
            connectionId = context.ConnectionId,
            inputs = context.Inputs,
            connection,
            transportRoute = new
            {
                recipient = $"authwire://{settings.GetProperty("host").GetString()}:{settings.GetProperty("port").GetInt32()}",
                addressTemplate = parameters.GetProperty("address").GetString(),
            },
        }, JsonSerializerOptions.Web));
    }
}

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Connections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModbusConnectionTestRequest(ModbusConnectionDraft Connection, long? ConnectionId = null);

internal static class ModbusConnectionTestEndpoints
{
    public static RouteGroupBuilder MapModbusConnectionTestEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/test/modbus", Test).WithTags("Connections")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("testModbusConnection")
            .WithSummary("通过共享会话测试未保存的 Modbus 连接参数，不发送协议报文或保存草稿")
            .WithDescription("复用已有流不代表重新验证远端存活；从站读取须通过设备预览。总期限包含共享串行队列等待。")
            .WithMetadata(new RequestBodyLimit(128 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<ModbusConnectionTestResult>, ValidationProblem, ProblemHttpResult>> Test(
        ModbusConnectionTestRequest request, ModbusConnectionTest probe, ConnectionDraftStore drafts,
        SnowflakeIdGenerator ids, PreviewOperationBudget budget, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var value = request.Connection ?? throw new ArgumentException("缺少 Modbus 参数。");
            var settings = new ModbusConnectionSettings
            {
                ConnectionId = request.ConnectionId ?? ids.NextId(),
                Host = value.Host,
                Port = value.Port,
                Format = value.Format,
                ConnectTimeoutMs = value.ConnectTimeoutMs,
                ReadTimeoutMs = value.ReadTimeoutMs,
                RequestIntervalMs = value.RequestIntervalMs,
                RtuSilenceMs = value.RtuSilenceMs
            }.Validate();
            if (request.ConnectionId is not null && !drafts.Read().Connections.Any(item => item.Id == request.ConnectionId && item.Kind == ConnectionKind.Modbus))
            { return ApiProblems.Problem(409, "connection.notFound", "请选择已有 Modbus 连接或创建新的测试表单。"); }
            return TypedResults.Ok(await probe.RunAsync(settings, context.RequestAborted));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["connection"] = ["请检查 Modbus 主机、端口、帧格式、超时与请求间隔。"] }); }
        catch (ModbusConnectionUnavailableException)
        { return ApiProblems.Problem(409, "connection.inUse", "当前会话配置不匹配、正在释放或连接数达到上限，未建立第二个会话。"); }
    }
}

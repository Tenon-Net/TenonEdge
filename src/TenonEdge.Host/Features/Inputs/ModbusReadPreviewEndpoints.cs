using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModbusReadPreviewRequest(string ExpectedVersion, long? InputId = null, ModbusSourceDraft? Source = null);

internal static class ModbusReadPreviewEndpoints
{
    internal static RouteGroupBuilder MapModbusReadPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/{deviceId:long}/preview/modbus", Read)
            .WithTags("Inputs").WithName("previewModbusDevice")
            .RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithSummary("显式读取 Modbus 草稿点表一次，不保存、不应用或入发送队列")
            .WithDescription("Source 提供未保存点表；否则按 InputId 读取一组，省略 InputId 读取全部启用组。连接与 UnitId 取该版本的已存设备草稿；共享总期限 30 秒与 64 KiB 显示容量。截断或未完成组不能当作完整样本。")
            .WithMetadata(new RequestBodyLimit(16 * 1024 * 1024))
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<ModbusDevicePreviewResult>, ValidationProblem, ProblemHttpResult>> Read(
        long deviceId, ModbusReadPreviewRequest request, InputDraftStore drafts, ModbusReadPreview preview,
        PreviewOperationBudget budget, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var snapshot = drafts.ReadModbusPreview(deviceId, request);
            return TypedResults.Ok(await preview.RunGroupsAsync(snapshot.Connection, snapshot.UnitId, snapshot.Groups, context.RequestAborted));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请核对已保存的 Modbus 设备、点表和启用点位。"] }); }
        catch (DraftWriteException exception)
        { return ApiProblems.Problem(exception.Code.EndsWith(".notFound", StringComparison.Ordinal) ? 404 : 409, exception.Code, "草稿已变化或对象不属于该设备，请刷新核对，未执行读取。"); }
        catch (ModbusConnectionUnavailableException)
        { return ApiProblems.Problem(409, "connection.inUse", "共享连接参数不匹配、正在释放或达到上限，未建立第二个会话。"); }
    }
}

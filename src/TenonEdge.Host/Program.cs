using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Health;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Parameters;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Features.Users;

// 只负责启动装配；服务注册、启动检查、请求管道和各功能端点分别在 Bootstrap 与 Features 中实现。
// 主机侧维护命令（例如 reset-admin）使用独立的通用宿主，不启动 Web 服务。
if (MaintenanceCommands.IsMaintenanceCommand(args))
{
    return await MaintenanceCommands.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddTenonEdgeHostServices();

var app = builder.Build();

// 取得数据目录的单实例锁等启动检查全部通过后，才开始接收请求；未通过时以约定的退出码结束。
var startupExitCode = await app.PrepareAsync();
if (startupExitCode != HostExitCodes.Success)
{
    return startupExitCode;
}

app.UseTenonEdgeRequestPipeline();

app.MapHealthEndpoints();
app.MapManagementApi()
    .MapSetupEndpoints()
    .MapAuthEndpoints()
    .MapUserEndpoints()
    .MapApiTokenEndpoints()
    .MapAuditEndpoints()
    .MapAuditExportEndpoints()
    .MapRuntimeEventEndpoints()
    .MapRuntimeStatusEndpoints()
    .MapRuntimeDeviceEndpoints()
    .MapRuntimeConnectionEndpoints()
    .MapRuntimeOutputEndpoints()
    .MapRuntimeMessageEndpoints()
    .MapConfigurationDraftEndpoints()
    .MapConfigurationExportEndpoints()
    .MapConfigurationImportEndpoints()
    .MapConfigurationOperationEndpoints()
    .MapConfigurationVersionEndpoints()
    .MapConfigurationReviewEndpoints()
    .MapConfigurationApplicationEndpoints()
    .MapConfigurationMigrationEndpoints()
    .MapConfigurationRuntimeEndpoints()
    .MapConfigurationRecoveryEndpoints()
    .MapSystemInfoEndpoints()
    .MapGatewayNameEndpoints()
    .MapRuntimeParameterEndpoints()
    .MapQueueParameterEndpoints()
    .MapTrustedProxyEndpoints()
    .MapOutboundInspectionEndpoints()
    .MapMqttCredentialEndpoints()
    .MapHttpCredentialEndpoints()
    .MapModuleCredentialEndpoints()
    .MapCredentialReviewEndpoints()
    .MapConnectionTrustEndpoints()
    .MapOutputControlEndpoints()
    .MapDeliveryInspectionEndpoints()
    .MapDeliveryRetryEndpoints()
    .MapDeliveryCleanupEndpoints()
    .MapDeliveryClearPendingEndpoints()
    .MapDeliveryExportEndpoints()
    .MapDeliveryMigrationEndpoints()
    .MapOutboundPolicyEndpoints()
    .MapParameterSchemaEndpoints()
    .MapConnectionDraftEndpoints()
    .MapMqttConnectionTestEndpoints()
    .MapHttpConnectionTestEndpoints()
    .MapModbusConnectionTestEndpoints()
    .MapDeviceDraftEndpoints()
    .MapRouteDraftEndpoints()
    .MapRouteCompileEndpoints()
    .MapRouteCatalogEndpoints()
    .MapRouteFieldEndpoints()
    .MapRoutePreviewEndpoints()
    .MapInputDraftEndpoints()
    .MapModbusReadPreviewEndpoints()
    .MapHttpReadPreviewEndpoints()
    .MapMqttReadPreviewEndpoints()
    .MapPointCsvEndpoints();

// 后台服务因未处理的异常结束时宿主随之停止，这里改用非零退出码，使服务管理器把它当作失败并重启（启动保护据此计数）。
// 在宿主停止之后、服务容器释放之前取得各后台服务的结果。
var exitCode = HostExitCodes.Success;
app.Lifetime.ApplicationStopped.Register(() => exitCode = HostExitCodes.AfterShutdown(app.Services.GetServices<IHostedService>()));
await app.RunAsync();
return exitCode;

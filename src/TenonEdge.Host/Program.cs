using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Health;

// 只负责启动装配；服务注册、请求管道和各功能端点分别在 Bootstrap 与 Features 中实现。
var builder = WebApplication.CreateBuilder(args);
builder.AddTenonEdgeHostServices();

var app = builder.Build();
app.UseTenonEdgeRequestPipeline();

app.MapHealthEndpoints();
app.MapManagementApi();

app.Run();

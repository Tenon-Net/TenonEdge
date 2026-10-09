using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Connections;

internal enum MqttTestAuthentication { None, Stored, Provided }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MqttConnectionTestRequest(MqttConnectionDraft Connection, MqttTestAuthentication Authentication = MqttTestAuthentication.None,
    long? CredentialConnectionId = null, string? Username = null, string? Password = null)
{
    public override string ToString() => "MQTT connection test request (secret omitted)";
}

internal static class MqttConnectionTestEndpoints
{
    public static RouteGroupBuilder MapMqttConnectionTestEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/test/mqtt", Test).WithTags("Connections")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("testMqttConnection")
            .WithSummary("测试未保存的 MQTT 参数到 CONNACK；独立 ClientId，不保存、不订阅、不发布")
            .WithDescription("认证显式选择 none、provided 或 stored；stored 只向凭证已绑定的接收方发送。返回分阶段结果和脱敏分类；MQTT 3.1.1 认证与协议握手共享 CONNACK 耗时，不可相加。")
            .WithMetadata(new RequestBodyLimit(32 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<MqttConnectionTestResult>, ValidationProblem, ProblemHttpResult>> Test(
        MqttConnectionTestRequest request, MqttConnectionTest probe, MqttCredentialStore credentials,
        PreviewOperationBudget budget, HttpContext context)
    {
        var session = context.User.FindFirstValue(SessionClaims.CsrfToken);
        if (string.IsNullOrEmpty(session))
        { return ApiProblems.Problem(403, "preview.sessionRequired", "真实测试需要已认证的本地会话。"); }
        using var lease = budget.TryAcquire(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session))));
        if (lease is null)
        { return ApiProblems.Problem(429, "preview.busy", "当前预览任务过多，请等待已有任务结束。"); }
        try
        {
            var value = request.Connection ?? throw new ArgumentException("缺少连接参数。");
            if (!Enum.IsDefined(request.Authentication) || value.Host != value.Host?.Trim() ||
                request.Authentication == MqttTestAuthentication.Stored &&
                    (request.CredentialConnectionId is null || request.Username is not null || request.Password is not null) ||
                request.Authentication != MqttTestAuthentication.Stored && request.CredentialConnectionId is not null ||
                request.Authentication == MqttTestAuthentication.None && (request.Username is not null || request.Password is not null) ||
                request.Authentication == MqttTestAuthentication.Provided && (request.Username is null || request.Password is null))
            { throw new ArgumentException("认证参数无效。"); }
            var settings = new MqttConnectionSettings
            {
                ConnectionId = request.CredentialConnectionId ?? 1,
                Host = value.Host!,
                Port = value.Port,
                Tls = value.Tls,
                ClientId = value.ClientId,
                TimeoutMs = value.TimeoutMs,
                PingIntervalMs = value.PingIntervalMs,
                UsePassword = request.Authentication != MqttTestAuthentication.None,
            }.ValidateConnection();
            // 只取一次接收方绑定的凭证快照；解析器在返回前关闭数据库客户端，不跨网络等待持有事务。
            var credential = request.Authentication switch
            {
                MqttTestAuthentication.Stored => credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient),
                MqttTestAuthentication.Provided => new MqttCredential(request.Username!, request.Password!, "transient-test", "transient-test"),
                _ => null,
            };
            if (settings.UsePassword && credential is null)
            { return ApiProblems.Problem(409, "credential.unavailable", "凭证不可用或接收方不匹配，请检查绑定或输入临时凭证。"); }
            return TypedResults.Ok(await probe.RunAsync(settings, credential, context.RequestAborted));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["connection"] = ["请检查 MQTT 地址、端口、超时和认证参数。"] }); }
    }
}

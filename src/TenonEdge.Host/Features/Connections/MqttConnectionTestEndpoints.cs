using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Connections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MqttConnectionTestRequest(MqttConnectionDraft Connection, ConnectionTestAuthentication Authentication = ConnectionTestAuthentication.None,
    long? CredentialConnectionId = null, string? Username = null, string? Password = null, long? ConnectionId = null, ClientCertificateUpload? Certificate = null)
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
            .WithMetadata(new RequestBodyLimit(256 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<MqttConnectionTestResult>, ValidationProblem, ProblemHttpResult>> Test(
        MqttConnectionTestRequest request, MqttConnectionTest probe, MqttCredentialStore credentials,
        PreviewOperationBudget budget, ConnectionDraftStore drafts, SnowflakeIdGenerator ids, TimeProvider time, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var lease = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var value = request.Connection ?? throw new ArgumentException("缺少连接参数。");
            if (!Enum.IsDefined(request.Authentication) || value.Host != value.Host?.Trim() ||
                request.ConnectionId is not null && request.CredentialConnectionId is not null && request.ConnectionId != request.CredentialConnectionId ||
                request.Authentication == ConnectionTestAuthentication.Stored &&
                    (request.CredentialConnectionId is null || request.Username is not null || request.Password is not null || request.Certificate is not null) ||
                request.Authentication != ConnectionTestAuthentication.Stored && request.CredentialConnectionId is not null ||
                request.Authentication == ConnectionTestAuthentication.None && (request.Username is not null || request.Password is not null || request.Certificate is not null) ||
                request.Authentication == ConnectionTestAuthentication.Provided &&
                    (request.Certificate is null ? request.Username is null || request.Password is null : request.Username is not null || request.Password is not null))
            { throw new ArgumentException("认证参数无效。"); }
            var settings = new MqttConnectionSettings
            {
                ConnectionId = request.ConnectionId ?? request.CredentialConnectionId ?? ids.NextId(),
                Host = value.Host!,
                Port = value.Port,
                Tls = value.Tls,
                ClientId = value.ClientId,
                TimeoutMs = value.TimeoutMs,
                PingIntervalMs = value.PingIntervalMs,
                UsePassword = request.Authentication != ConnectionTestAuthentication.None,
            }.ValidateConnection();
            if (request.ConnectionId is not null && !drafts.Read().Connections.Any(item => item.Id == settings.ConnectionId && item.Kind == ConnectionKind.Mqtt))
            { return ApiProblems.Problem(409, "connection.notFound", "请选择已有 MQTT 连接或创建新的测试表单。"); }
            // 只取一次接收方绑定的凭证快照；解析器在返回前关闭数据库客户端，不跨网络等待持有事务。
            var credential = request.Authentication switch
            {
                ConnectionTestAuthentication.Stored => credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient),
                ConnectionTestAuthentication.Provided => Transient(request, time.GetUtcNow()),
                _ => null,
            };
            if (settings.UsePassword && credential is null)
            { return ApiProblems.Problem(409, "credential.unavailable", "凭证不可用或接收方不匹配，请检查绑定或输入临时凭证。"); }
            return TypedResults.Ok(await probe.RunAsync(settings, credential, context.RequestAborted));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["connection"] = ["请检查 MQTT 地址、端口、超时和认证参数。"] }); }
    }

    private static MqttCredential Transient(MqttConnectionTestRequest request, DateTimeOffset now)
    {
        if (request.Certificate is null)
        { return new(request.Username!, request.Password!, "transient-test", "transient-test"); }
        using var material = request.Certificate.Open(now);
        var data = material.ExportPkcs12();
        try
        { return new(string.Empty, Convert.ToBase64String(data), "transient-test", "transient-test", MqttCredentialKind.ClientCertificate, material.Info.Subject); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Credentials;

internal static class ClientCertificateReviewEndpoints
{
    public static RouteGroupBuilder MapClientCertificateReviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/{connectionId:long}/client-certificate-review", Review).WithTags("Credentials")
            .RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithMetadata(new RequestBodyLimit(256 * 1024))
            .WithName("reviewClientCertificateCredential").WithSummary("核对待上传客户端证书的公开身份与当前积存，不保存或联网")
            .WithDescription("仅供保存前核对，不冻结队列或授权保存。确认仍调用原凭证写入接口，重新验证草稿、凭证版本、证书和积存声明。")
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static Results<Ok<ClientCertificateReview>, ValidationProblem, ProblemHttpResult> Review(long connectionId,
        ClientCertificateReviewRequest request, ConfigDatabase database, DeliveryCache deliveries,
        PreviewOperationBudget budget, TimeProvider time, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            if (connectionId is < 1 or > 9007199254740991L || request.Certificate is null || request.Kind is not (ConnectionKind.Http or ConnectionKind.Mqtt))
            { throw new ArgumentException("请选择 HTTP 或 MQTT 草稿连接与客户端证书。"); }
            var mqtt = request.Kind == ConnectionKind.Mqtt;
            var recipient = mqtt ? MqttCredentialStore.NormalizeRecipient(request.Recipient) : HttpCredentialStore.NormalizeRecipient(request.Recipient);
            if (!recipient.StartsWith(mqtt ? "mqtts://" : "https://", StringComparison.Ordinal))
            { throw new ArgumentException("客户端证书只用于 TLS 连接。"); }
            using var material = request.Certificate.Open(time.GetUtcNow());
            // One short configuration snapshot. No private material is exported or persisted.
            var current = database.InTransaction(client =>
            {
                CredentialDraftGuard.Check(client, connectionId, request.Kind, recipient, request.ExpectedDraftVersion, requireDraft: true);
                var httpRecord = client.Queryable<HttpCredentialRecord>().InSingle(connectionId);
                var mqttRecord = client.Queryable<MqttCredentialRecord>().InSingle(connectionId);
                if (mqtt && httpRecord is not null || !mqtt && mqttRecord is not null)
                { throw new CredentialWriteException("credential.protocolConflict"); }
                var version = mqtt ? mqttRecord?.Version : httpRecord?.Version;
                if (version != request.ExpectedVersion)
                { throw new CredentialWriteException("credential.versionConflict"); }
                return mqtt
                    ? mqttRecord is null ? null : new CredentialAccountReview(mqttRecord.Recipient,
                        mqttRecord.Kind == (int)MqttCredentialKind.ClientCertificate ? "clientCertificate" : "password",
                        mqttRecord.Kind == (int)MqttCredentialKind.ClientCertificate ? mqttRecord.CertificateSubject : mqttRecord.Username,
                        mqttRecord.ProtectedPassword is not null)
                    : httpRecord is null ? null : new CredentialAccountReview(httpRecord.Recipient,
                        JsonNamingPolicyKind((HttpCredentialKind)httpRecord.Kind),
                        httpRecord.Kind == (int)HttpCredentialKind.ClientCertificate ? httpRecord.CertificateSubject : httpRecord.Username,
                        httpRecord.ProtectedSecret is not null);
            });
            if (deliveries.GetStatus().Store is null)
            { throw new CredentialWriteException("credential.queueUnavailable"); }
            var report = deliveries.GetQueueReport([connectionId], connectionId - 1, 1);
            var usage = report.Targets.Single(item => item.ConnectionId == connectionId).Usage;
            var identityChanged = current is null || current.Recipient != recipient || current.Kind != "clientCertificate" || current.Account != material.Info.Subject;
            // Match the original write guard, including invalidated records and changed recipients.
            var declarationRequired = current?.Kind == "clientCertificate" && current.Account == material.Info.Subject &&
                usage.PendingCount + usage.FailedCount > 0;
            return TypedResults.Ok(new ClientCertificateReview(connectionId, recipient, request.ExpectedDraftVersion, request.ExpectedVersion,
                material.Info, current, identityChanged, declarationRequired, usage.PendingCount, usage.FailedCount, report.CapturedAt));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["certificate"] = ["请检查 TLS 草稿连接及证书格式、大小、私钥、有效期和客户端认证用途。"] }); }
        catch (CredentialWriteException exception)
        { return ApiProblems.Problem(409, exception.Code, "无法核对客户端证书，请重新读取草稿、凭证状态和队列后再试。"); }
    }

    private static string JsonNamingPolicyKind(HttpCredentialKind kind) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClientCertificateReviewRequest(ConnectionKind Kind, string Recipient, string ExpectedDraftVersion,
    string? ExpectedVersion, ClientCertificateUpload Certificate)
{
    public override string ToString() => "Client certificate review (private material omitted)";
}

internal sealed record CredentialAccountReview(string Recipient, string Kind, string? Account, bool Configured);
internal sealed record ClientCertificateReview(long ConnectionId, string Recipient, string ExpectedDraftVersion, string? ExpectedVersion,
    ClientCertificateInfo Certificate, CredentialAccountReview? Current, bool IdentityChanged, bool AccountDeclarationRequired,
    long PendingCount, long FailedCount, DateTimeOffset CapturedAt);

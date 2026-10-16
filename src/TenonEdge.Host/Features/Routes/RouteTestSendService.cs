using System.Globalization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteTestSendResult(Guid Id, string Kind, string Code, HttpTestSendResult? Http = null, MqttTestSendResult? Mqtt = null)
{
    public override string ToString() => $"Route test send ({Id}, {Kind}, {Code}; body omitted)";
}

/// <summary>确认后的单次发送。单例只持工厂/线程安全服务，所有网络等待均在配置事务结束之后。</summary>
internal sealed class RouteTestSendService(RouteTestSendArtifacts artifacts, ConfigDatabase database, HttpCredentialStore httpCredentials,
    MqttCredentialStore mqttCredentials, HttpTestMessageSender httpSender, MqttTestMessageSender mqttSender, AuditTrail audit, TimeProvider time)
{
    internal RouteTestSendReview Prepare(Guid id, string owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var artifact = artifacts.Read(id, owner);
        if (artifact.Output.Kind == "output.http")
        {
            var settings = HttpOutputSettings.Parse(artifact.Configuration);
            CheckDraft(artifact, settings.ConnectionId);
            var identity = httpCredentials.DescribeHttp(settings.ConnectionId);
            return artifacts.Prepare(id, owner, artifact, settings.ConnectionId, settings.BaseAddress,
                identity?.Kind.ToString().ToLowerInvariant() ?? "none",
                identity?.Kind == HttpCredentialKind.ClientCertificate ? identity.CertificateSubject : identity?.Username, identity, null);
        }
        if (artifact.Output.Kind != "output.mqtt")
        { throw new ArgumentException("仅通用 HTTP/MQTT 输出可发送测试报文。"); }
        var mqtt = MqttOutputSettings.Parse(artifact.Configuration);
        CheckDraft(artifact, mqtt.ConnectionId);
        var account = mqttCredentials.DescribeMqtt(mqtt.ConnectionId);
        if (mqtt.UsePassword && account is null)
        { throw Changed(); }
        return artifacts.Prepare(id, owner, artifact, mqtt.ConnectionId, mqtt.Recipient,
            account?.Kind == MqttCredentialKind.ClientCertificate ? "clientcertificate" : account is null ? "none" : "password",
            account?.Kind == MqttCredentialKind.ClientCertificate ? account.CertificateSubject : account?.Username, null, account);
    }

    internal async Task<RouteTestSendResult> ConfirmAsync(Guid id, Guid confirmationId, bool confirmed, string owner,
        ManagementActor actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = artifacts.Consume(id, confirmationId, owner, confirmed);
        var artifact = selected.Artifact;
        var review = selected.Review;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var target = review.ConnectionId.ToString(CultureInfo.InvariantCulture);
        var detail = $"test={id:D}; delivery={review.DeliveryId:D}; bytes={review.BodyBytes}";
        AuditAccount? account = null;
        var code = "deliveryUnknown";
        try
        {
            HttpCredential? httpCredential = null;
            MqttCredential? mqttCredential = null;
            HttpOutputSettings? http = null;
            MqttOutputSettings? mqtt = null;
            OutputMessage message;
            if (artifact.Output.Kind == "output.http")
            {
                http = HttpOutputSettings.Parse(artifact.Configuration);
                var identity = httpCredentials.DescribeHttp(http.ConnectionId);
                if (identity != selected.HttpIdentity)
                { throw Changed(); }
                httpCredential = identity is null ? null : httpCredentials.ResolveHttp(http.ConnectionId, http.BaseAddress);
                if (identity is not null && httpCredential?.Identity != identity)
                { throw Changed(); }
                message = http.FreezePayload(artifact.Payload, artifact.Output.DeliveryId, identity);
            }
            else
            {
                mqtt = MqttOutputSettings.Parse(artifact.Configuration);
                var identity = mqttCredentials.DescribeMqtt(mqtt.ConnectionId);
                if (identity != selected.MqttIdentity || mqtt.UsePassword && identity is null)
                { throw Changed(); }
                mqttCredential = identity is null ? null : mqttCredentials.ResolveMqtt(mqtt.ConnectionId, mqtt.Recipient);
                if (identity is not null && mqttCredential?.Identity != identity)
                { throw Changed(); }
                message = new(artifact.Payload, artifact.Output.Address, artifact.Output.Qos,
                    mqtt.Fingerprint(identity), "test-send");
            }

            // 审计开始记录必须先持久成功，否则不发送；同时复核操作者、草稿与凭证版本。
            // 审计只记录固定分类/编号/字节数，不含正文、头、地址、秘密或对方响应。
            account = database.InTransaction(client =>
            {
                operation.Token.ThrowIfCancellationRequested();
                ConfigurationDraft.CheckVersion(client, artifact.Version);
                RequireCredentialComplete(client, review.ConnectionId);
                var version = http is not null ? client.Queryable<HttpCredentialRecord>().InSingle(review.ConnectionId)?.Version
                    : client.Queryable<MqttCredentialRecord>().InSingle(review.ConnectionId)?.Version;
                if (version != (selected.HttpIdentity?.Version ?? selected.MqttIdentity?.Version))
                { throw Changed(); }
                var current = actor.RequireConfigurator(client, time.GetUtcNow().ToUnixTimeMilliseconds());
                audit.Append(client, new("route.testSend.start", "started", current, actor.SourceIp, target, detail));
                return current;
            });
            operation.Token.ThrowIfCancellationRequested();
            if (http is not null)
            {
                var result = await httpSender.SendAsync(http, httpCredential, message, operation.Token);
                code = result.Code;
                if (result.StatusCode is { } status)
                { detail += $"; httpStatus={status.ToString(CultureInfo.InvariantCulture)}"; }
                return new(id, artifact.Output.Kind, code, Http: result);
            }
            var published = await mqttSender.SendAsync(mqtt!, mqttCredential, message, operation.Token);
            code = published.Code;
            detail += published.BrokerAcknowledged ? "; brokerAcknowledged=true" : "; brokerAcknowledged=false";
            return new(id, artifact.Output.Kind, code, Mqtt: published);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { code = "deadlineUnknown"; return new(id, artifact.Output.Kind, code); }
        catch (OperationCanceledException) { code = "canceledUnknown"; throw; }
        catch (HttpConnectionUnavailableException) { code = "connection.inUse"; throw; }
        finally
        {
            if (account is { } startedBy)
            { audit.AppendOrLog(new("route.testSend.complete", code, startedBy, actor.SourceIp, target, detail)); }
        }
    }

    private void CheckDraft(RouteTestSendArtifact artifact, long connectionId) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, artifact.Version);
        RequireCredentialComplete(client, connectionId);
        return true;
    });
    private static void RequireCredentialComplete(SqlSugar.SqlSugarClient client, long id)
    {
        if (client.Queryable<CredentialRequirementRecord>().Where(value => value.ConnectionId == id).Any())
        { throw new DraftWriteException("configuration.credentialsRequired"); }
    }
    private static RouteTestSendException Changed() => new(409, "testSend.credentialChanged");
}

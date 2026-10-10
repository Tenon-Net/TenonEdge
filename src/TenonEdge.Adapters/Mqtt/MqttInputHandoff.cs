using System.Buffers;
using System.Text.Json;
using System.Text.Unicode;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>接收器的持久接管绑定：预算预约后解码，不持有共享连接执行器，也不自行确认或重连。</summary>
internal sealed class MqttInputHandoff(InputUnitContext context, IJsonInputDecoder decoder)
{
    // 与 HTTP JSON 输入相同的正文、解析树、字段结果及独立 raw 副本上界。
    internal const long ReservationBytes = 48L * 1024 * 1024;
    internal const long MessageBytes = 4 * 1024 * 1024 + 8192 + RawJsonPayload.MaximumBudgetBytes;

    public Task<InputHandoffStatus> ReceiveAsync(MqttApplicationMessage message, CancellationToken cancellationToken) =>
        ReceiveAsync(message, null, cancellationToken);

    public async Task<InputHandoffStatus> ReceiveAsync(MqttApplicationMessage message, MqttCredential? credential, CancellationToken cancellationToken)
    {
        var receivedAt = context.Time.GetUtcNow();
        if (message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce)
        {
            var discarded = false;
            var accepted = context.Messages.TryProduce(ReservationBytes, MessageBytes, () =>
            {
                var decoded = Decode(cancellationToken);
                discarded = decoded is null;
                return decoded;
            }, cancellationToken);
            if (!accepted)
            { context.Reads.CycleSkipped(receivedAt); }
            return accepted && !discarded ? InputHandoffStatus.Buffered : InputHandoffStatus.Rejected;
        }
        return await context.Messages.ProduceConfirmedAsync(ReservationBytes, MessageBytes,
            token => ValueTask.FromResult(Decode(token)), cancellationToken);

        EdgeMessage? Decode(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (message.Payload.Length > MqttInputReceiver.MaximumPayloadBytes)
                { throw new InputMessageSizeException("MQTT 输入正文超过 1 MiB。"); }
                var bytes = message.Payload.ToArray();
                if (!Utf8.IsValid(bytes))
                { throw new JsonException(); }
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                var secrets = credential is null ? Array.Empty<string>() : new[] { credential.Password, Uri.EscapeDataString(credential.Password) };
                var access = JsonReadPreview.IsSensitive(document.RootElement, null, token, secrets)
                    ? DiagnosticSampleAccess.Redacted : DiagnosticSampleAccess.Safe;
                var fields = decoder.Decode(document.RootElement, receivedAt, context.ClockState, token);
                token.ThrowIfCancellationRequested();
                var edge = new EdgeMessage(Guid.CreateVersion7(receivedAt), context.InputKey, context.DeviceKey,
                    fields.CollectedAt, fields.TimeSource, receivedAt, fields.Values, fields.Quality,
                    RawJsonPayload.CopyFrom(document.RootElement), access,
                    new(fields.InvalidUpstreamTime, new(message.Retain, message.Dup, (int)message.QualityOfServiceLevel)));
                if (fields.InvalidUpstreamTime)
                { context.Reads.UpstreamTimeInvalid(); }
                context.Reads.ReadCompleted(receivedAt, fields.Quality.Count);
                return edge;
            }
            catch (Exception exception) when (exception is JsonException or InputMessageSizeException)
            {
                context.Reads.ReadFailed(receivedAt, "MQTT 输入正文或字段无效/超限。", InputReadFailureKind.DataError);
                return null;
            }
        }
    }
}

using System.Text;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Outputs;

public sealed partial class OutputModuleCatalog
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>直接使用原传输的纯格式化入口，不构建运行目标或计划工厂、不解析凭证、不创建租约。
    /// null 明示没有此能力；调用方不得回退到另一协议或现场发送。</summary>
    public OutputMessageContent? FormatMessage(RuntimeUnitPlan unit, EdgeMessage message, Guid deliveryId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var protocol = ReadProtocol(unit) ?? unit;
            var route = OutputTransportRoute.Read(protocol.Configuration, protocol.Id.Kind, required: true)!;
            if (!_transports.TryGetValue(protocol.Id.Kind, out var factory))
            { return null; }
            // Match Factory.Build: the protocol configuration retains the original module unit ID.
            var content = factory.FormatMessage(new(unit.Id, protocol.Configuration), message, deliveryId, token);
            token.ThrowIfCancellationRequested();
            if (content is null)
            { return null; }
            if (content.Payload.IsEmpty || content.Payload.Length > StandardEnvelope.MaximumPayloadBytes)
            { throw new InputMessageSizeException("扩展输出报文超过纯预览预算或为空。"); }
            if (content.Address != route.AddressFor(message.DeviceKey) || content.QualityOfService is not (null or 0 or 1))
            { throw Invalid(); }
            // Never silently replace invalid UTF-8 with U+FFFD and claim to show the actual bytes.
            _ = StrictUtf8.GetCharCount(content.Payload.Span);
            if (content.Headers is { } headers)
            {
                if (headers.Count > 64)
                { throw new InputMessageSizeException("扩展输出头超过纯预览预算。"); }
                long bytes = 0;
                foreach (var (name, value) in headers)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || value is null || value.Any(char.IsControl))
                    { throw Invalid(); }
                    bytes += StrictUtf8.GetByteCount(name) + (long)StrictUtf8.GetByteCount(value);
                    if (bytes > 16 * 1024)
                    { throw new InputMessageSizeException("扩展输出头超过纯预览预算。"); }
                }
            }
            if (content.ProtocolMetadata is { } metadata && StrictUtf8.GetByteCount(metadata) > OutputMessage.MaximumProtocolMetadataBytes)
            { throw new InputMessageSizeException("扩展输出协议元数据超过纯预览预算。"); }
            token.ThrowIfCancellationRequested();
            return content;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InputMessageSizeException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }
}

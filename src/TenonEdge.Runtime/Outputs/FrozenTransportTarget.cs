using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Outputs;

/// <summary>将扩展目标与不执行模块代码即可读取的冻结路由对应起来；不持有连接或另建发送者。</summary>
internal sealed class FrozenTransportTarget : IOutputTarget
{
    private readonly IOutputTarget _inner;
    private readonly OutputTransportRoute _route;
    private readonly string _protocol;

    public FrozenTransportTarget(IOutputTarget inner, OutputTransportRoute route, string protocol)
    {
        _inner = inner;
        _route = route;
        _protocol = protocol;
        _ = CaptureIdentity();
    }

    public RuntimeUnitId Id => _inner.Id;
    public long ConnectionId => _inner.ConnectionId;
    public IReadOnlyList<string> Inputs => _inner.Inputs;
    public string? ClientId => _inner.ClientId;
    public string Recipient => _route.Recipient;
    public string CredentialVersion => _inner.CredentialVersion;
    public string CurrentFingerprint => CaptureIdentity().Fingerprint;
    public TimeSpan SendTimeout => _inner.SendTimeout;
    // Include bounded summary parsing and address expansion in the original per-path reservation.
    public long FormattingReservationBytes => checked(_inner.FormattingReservationBytes +
        8L * OutputRecipientIdentity.MaximumSummaryBytes + 2L * OutputTransportRoute.MaximumAddressBytes);
    public long MinimumSerializationReservationBytes => _inner.MinimumSerializationReservationBytes;
    public bool QuiesceWithInputs => _inner.QuiesceWithInputs;

    public OutputTargetIdentity CaptureIdentity()
    {
        var identity = _inner.CaptureIdentity();
        if (_inner.Recipient != _route.Recipient || identity is null || string.IsNullOrWhiteSpace(identity.Fingerprint) ||
            identity.Recipient.Protocol != _protocol || identity.Recipient.Address != _route.Recipient)
        { throw Invalid(); }
        _ = identity.Summary;
        return identity;
    }

    public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
    {
        var output = _inner.CreateMessage(message, deliveryId);
        if (output is null || output.Address != _route.AddressFor(message.DeviceKey) ||
            string.IsNullOrWhiteSpace(output.Fingerprint) || OutputRecipientIdentity.Read(output.FingerprintSummary) is not { } identity ||
            identity.Protocol != _protocol || identity.Address != _route.Recipient)
        { throw Invalid(); }
        return output;
    }

    // Do not revalidate each sent record against AddressTemplate: explicitly migrated records
    // retain their old address and bytes. The existing sender checks the migrated fingerprint.
    public IOutputAdapter CreateAdapter() => _inner.CreateAdapter();

    public bool CanShareSenderWith(IOutputTarget other) => other is FrozenTransportTarget target &&
        _protocol == target._protocol && _route.Recipient == target._route.Recipient && _inner.CanShareSenderWith(target._inner);

    private static ArgumentException Invalid() => new("扩展输出目标或报文与冻结路由不一致。");
}

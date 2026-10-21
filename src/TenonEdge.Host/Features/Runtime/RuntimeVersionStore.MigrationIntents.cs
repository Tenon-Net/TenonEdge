using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Deliveries;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeVersionStore
{
    // 只允许同一启用输出连接换接收地址；不能用一个迁移声明同时绕过删连接、改协议或 Topic/相对路径的保护。
    // 原指纹必须由协调者从原发送者捕获；这里验证确认资料与两个不可变计划的非秘密地址一致。
    private static HashSet<long> ValidateMigrationIntents(RuntimePlanDocument? previous, RuntimePlanDocument next,
        IReadOnlyList<DeliveryMigrationIntentDraft> intents, string? configStoreId)
    {
        if (intents.Count == 0)
        { return []; }
        if (previous is null || intents.Count > 500 || intents.Any(value => value is null || value.Hold is null || value.Source is null || value.Target is null || value.Target.Recipient is null) ||
            intents.Select(value => value.Hold.ConnectionId).Distinct().Count() != intents.Count ||
            intents.Select(value => value.Hold.IntentId).Distinct().Count() != intents.Count)
        { throw new InvalidOperationException("迁移意图清单无效。"); }
        var before = MigrationRecipients(previous).ToLookup(value => value.ConnectionId);
        var after = MigrationRecipients(next).ToLookup(value => value.ConnectionId);
        foreach (var intent in intents)
        {
            var old = before[intent.Hold.ConnectionId].ToArray();
            var current = after[intent.Hold.ConnectionId].ToArray();
            _ = intent.Source.Freeze();
            _ = intent.Target.Summary;
            if (string.IsNullOrWhiteSpace(intent.Target.Fingerprint) || intent.Target.Recipient.Protocol != intent.Source.Protocol ||
                intent.Target.Recipient.Address != intent.TargetRecipient || !intent.Target.Recipient.AccountRecorded ||
                !intent.SameOwnerConfirmed || intent.Hold.ConfigStoreId != configStoreId ||
                old.Length == 0 || current.Length == 0 || old.Any(value => value.Protocol != intent.Source.Protocol || value.Recipient != intent.Source.Address) ||
                current.Any(value => value.Protocol != intent.Source.Protocol || value.Recipient != intent.TargetRecipient) ||
                intent.Source.Address == intent.TargetRecipient ||
                (!old.All(value => value.QueueOnly) && !current.All(value => value.QueueOnly) &&
                    !old.Where(value => !value.QueueOnly).Select(value => value.Address).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(current.Where(value => !value.QueueOnly).Select(value => value.Address))))
            { throw new InvalidOperationException("迁移只允许已确认同一所有者的同协议连接换地址，不允许删除目标或夹带 Topic/路径修改。"); }
        }
        return intents.Select(value => value.Hold.ConnectionId).ToHashSet();
    }

    private static IEnumerable<(long ConnectionId, string Protocol, string Recipient, string Address, bool QueueOnly)> MigrationRecipients(RuntimePlanDocument document)
    {
        foreach (var unit in document.Units.Select(RuntimeConnectionReferences.Protocol))
        {
            if (unit.Id.Kind == "output.http")
            {
                var settings = HttpOutputSettings.Parse(unit.Configuration);
                yield return (settings.ConnectionId, "http", settings.BaseAddress, settings.Path, settings.QueueOnly);
            }
            else if (unit.Id.Kind == "output.mqtt")
            {
                var settings = MqttOutputSettings.Parse(unit.Configuration);
                yield return (settings.ConnectionId, "mqtt", settings.Recipient, settings.Topic, settings.QueueOnly);
            }
            else if (OutputTransportRoute.Read(unit.Configuration, unit.Id.Kind) is { } route)
            {
                yield return (unit.Configuration.GetProperty("connectionId").GetInt64(), unit.Id.Kind[7..], route.Recipient, route.AddressTemplate,
                    unit.Configuration.TryGetProperty("queueOnly", out var queueOnly) && queueOnly.ValueKind == JsonValueKind.True);
            }
        }
    }
}

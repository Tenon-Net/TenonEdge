using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Host.Features.Routes;

internal static partial class RoutePlanCompiler
{
    // 旧 schema 2 的完整检查点已冻结了未引用连接，但旧编译器没有生成发送接续单元。
    // 仅从通过哈希校验的检查点补单元，不重新编译路线、不读取当前草稿、不改写历史内容/哈希。
    internal static RuntimePlanDocument WithQueueOutputs(RuntimePlanDocument plan, ConfigurationCheckpoint draft)
    {
        try
        {
            var credentials = draft.CredentialConnections?.ToHashSet();
            var units = plan.Units.ToList();
            var additions = QueueOutputs(draft.Connections, units, connectionId =>
            {
                if (credentials is not null)
                { return credentials.Contains(connectionId); }
                // 更早的检查点没有凭证引用；已有输入明确记录的认证要求仍可复用。
                var input = units.Select(RuntimeConnectionReferences.Protocol).FirstOrDefault(unit => unit.Id.Kind == "input.mqtt" &&
                    unit.Configuration.GetProperty("connectionId").GetInt64() == connectionId);
                if (input is not null)
                { return MqttInputSettings.Parse(input.Configuration).UsePassword; }
                return null; // 只降级这个发送单元，不能把整个旧快照判损坏或猜成匿名连接。
            }, CancellationToken.None).ToArray();
            if (additions.Length == 0)
            { return plan; }
            units.AddRange(additions);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, unit.Configuration })
            }, RouteSourceCompiler.Json);
            if (bytes.LongLength > RuntimePlanDocument.MaximumBytes)
            { throw new RuntimePlanDocumentException("发送接续后的运行计划超过 16 MiB。"); }
            return RuntimePlanDocument.ParseStored(bytes);
        }
        catch (Exception error) when (error is RouteTopologyException || ConfigurationError(error))
        { throw new RuntimePlanDocumentException("完整配置中的发送接续参数无效。"); }
    }

    private static IEnumerable<RuntimeUnitPlan> QueueOutputs(IEnumerable<ConnectionDraft> connections,
        IReadOnlyList<RuntimeUnitPlan> units, Func<long, bool?> usesPassword, CancellationToken token, OutputModuleCatalog? outputs = null)
    {
        var all = connections.ToArray();
        if (all.Length > 500 || all.Any(value => value is null) || all.Select(value => value.Id).Distinct().Count() != all.Length)
        { throw Error("plan.configuration", ""); }
        var targets = units.Select(RuntimeConnectionReferences.Protocol).Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal) &&
            unit.Configuration.TryGetProperty("connectionId", out _))
            .Select(unit => unit.Configuration.GetProperty("connectionId").GetInt64()).ToHashSet();
        foreach (var connection in all.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!connection.Enabled || targets.Contains(connection.Id))
            { continue; }
            if (connection.Kind == ConnectionKind.Module && connection.Module is { } module && outputs is not null)
            {
                RuntimeUnitPlan? queue;
                try
                {
                    if (!BusinessKeys.IsValid(connection.Key))
                    { throw new ArgumentException("连接 key 无效。"); }
                    queue = outputs.WithQueueProtocol(RuntimeUnitId.Create("output." + module.Kind, "queue/" + connection.Key + "/sender"), module.Snapshot(connection.Id, all.ToDictionary(value => value.Id), all.Where(value => usesPassword(value.Id) == true).Select(value => value.Id).ToHashSet(), requireEnabled: true));
                }
                catch (Exception error) when (ConfigurationError(error))
                { throw Error("output.configuration", ""); }
                if (queue is not null)
                { yield return queue; }
                continue;
            }
            if (connection.Kind is not (ConnectionKind.Mqtt or ConnectionKind.Http))
            { continue; }
            // 三段标识与正常路线的两段标识不重合；是连接的发送接续单元，不增加路线/来源/路径计数。
            if (!BusinessKeys.IsValid(connection.Key))
            { throw Error("output.configuration", ""); }
            var kind = connection.Kind == ConnectionKind.Mqtt ? "output.mqtt" : "output.http";
            var id = RuntimeUnitId.Create(kind, "queue/" + connection.Key + "/sender");
            JsonElement configuration;
            try
            {
                if (connection.Kind == ConnectionKind.Mqtt && connection.Mqtt is { } mqtt)
                {
                    var usePassword = usesPassword(connection.Id);
                    configuration = JsonSerializer.SerializeToElement(new MqttOutputSettings
                    {
                        QueueOnly = true,
                        ConnectionId = connection.Id,
                        Host = mqtt.Host,
                        Port = mqtt.Port,
                        Tls = mqtt.Tls,
                        ClientId = mqtt.ClientId,
                        TimeoutMs = mqtt.TimeoutMs,
                        PingIntervalMs = mqtt.PingIntervalMs,
                        UsePassword = usePassword ?? false,
                        QueueCredentialReferenceMissing = usePassword is null,
                    }, RouteSourceCompiler.SettingsJson);
                    _ = MqttOutputSettings.Parse(configuration);
                }
                else if (connection.Kind == ConnectionKind.Http && connection.Http is { } http)
                {
                    configuration = JsonSerializer.SerializeToElement(new HttpOutputSettings
                    {
                        QueueOnly = true,
                        ConnectionId = connection.Id,
                        BaseAddress = http.BaseAddress,
                        TimeoutMs = http.TimeoutMs,
                    }, RouteSourceCompiler.SettingsJson);
                    _ = HttpOutputSettings.Parse(configuration);
                }
                else
                { throw new ArgumentException("连接协议与非秘密参数不一致。"); }
            }
            catch (Exception error) when (ConfigurationError(error))
            { throw Error("output.configuration", "", id.Key); }
            yield return new(id, configuration);
        }
    }
}

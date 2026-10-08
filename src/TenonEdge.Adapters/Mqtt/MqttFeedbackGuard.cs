using MQTTnet;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>拒绝当前计划中按已知 Broker 地址和 Topic Filter 可证明的 MQTT 反馈闭环。
/// 不做 DNS/网络探测，无法推断不同地址背后是否为同一个 Broker。</summary>
public static class MqttFeedbackGuard
{
    public static void Validate(IEnumerable<RuntimeUnitPlan> units)
    {
        var plan = units.ToArray();
        var inputs = plan.Where(unit => unit.Id.Kind == "input.mqtt")
            .Select(unit => (Key: unit.Id.Key, Settings: MqttInputSettings.Parse(unit.Configuration))).ToArray();
        var nodes = inputs.Select(input => input.Key).ToHashSet(StringComparer.Ordinal);
        var edges = nodes.ToDictionary(key => key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var recipients = inputs.ToLookup(input => input.Settings.Recipient, StringComparer.Ordinal);
        foreach (var unit in plan.Where(unit => unit.Id.Kind == "output.mqtt"))
        {
            var output = MqttOutputSettings.Parse(unit.Configuration);
            foreach (var source in output.Inputs.Where(nodes.Contains))
            {
                var topic = output.TopicFor(source.Split('/')[0]);
                foreach (var input in recipients[output.Recipient])
                {
                    if (MqttTopicFilterComparer.Compare(topic, input.Settings.TopicFilter) == MqttTopicFilterCompareResult.IsMatch)
                    { edges[source].Add(input.Key); }
                }
            }
        }
        // 拓扑剥离避免递归深度随输入数量增长；并行重复路径只算一条邻接边。
        var indegree = nodes.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var targets in edges.Values)
        {
            foreach (var target in targets)
            { indegree[target]++; }
        }
        var ready = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var source))
        {
            visited++;
            foreach (var target in edges[source])
            {
                if (--indegree[target] == 0)
                { ready.Enqueue(target); }
            }
        }
        if (visited != nodes.Count)
        { throw new ArgumentException("MQTT 输入与输出存在已知反馈闭环，请调整发布主题或订阅过滤器。"); }
    }
}

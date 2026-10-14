using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Lifecycle;

public sealed record RuntimeMessageCatalog(long? Revision, ImmutableArray<RuntimeUnitId> Objects);

public sealed partial class RuntimeHost
{
    private RuntimeMessageCatalog _messageCatalog = new(null, []);

    /// <summary>目录来自当前不可变计划，不从共享发送者的实例 ID 猜测。只含诊断身份，不公开配置。
    /// 每次激活构建一次，查询只返回不可变目录；queue-only 接续单元不生成消息。</summary>
    public RuntimeMessageCatalog GetMessageCatalog()
    {
        lock (_lock)
        { return _messageCatalog; }
    }

    // Called under the lifecycle lock after old producers are quiescent.
    private void ActivateMessageDiagnostics(RuntimePlan plan)
    {
        var objects = plan.Units.Where(unit => unit.Id.Kind.StartsWith(InputKindPrefix, StringComparison.Ordinal) ||
            unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal) &&
            !(unit.Configuration.TryGetProperty("queueOnly", out var value) && value.ValueKind == JsonValueKind.True))
            .Select(unit => plan.DiagnosticObject(unit.Id)).Distinct()
            .OrderBy(id => id.ToString(), StringComparer.Ordinal).ToImmutableArray();
        Messages.RetainObjects(objects.ToHashSet());
        _messageCatalog = new(plan.Revision, objects);
    }
}

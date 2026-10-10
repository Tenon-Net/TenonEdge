using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>版本 1 保持种子计划兼容；版本 2 同时冻结完整草稿和已编译计划，统一核对整个内容哈希。</summary>
internal sealed record RuntimeSnapshotDocument(int SchemaVersion, string Content, string ContentHash)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static RuntimeSnapshotDocument FromPlan(RuntimePlanDocument plan) => new(1, plan.CanonicalText, plan.ContentHash);

    public static RuntimeSnapshotDocument Create(RuntimePlanDocument plan, ConfigurationCheckpoint draft)
    {
        using var parsed = JsonDocument.Parse(plan.Canonical);
        var content = JsonSerializer.Serialize(new Envelope(2, parsed.RootElement, draft), Json);
        CheckSize(content);
        return new(2, content, RuntimePlanDocument.HashOf(content));
    }

    public static RuntimePlanDocument ReadPlan(RuntimeSnapshotRecord snapshot) => Read(snapshot).Plan;

    public static (RuntimePlanDocument Plan, ConfigurationCheckpoint? Draft) Read(RuntimeSnapshotRecord snapshot)
    {
        CheckSize(snapshot.Content);
        if (RuntimePlanDocument.HashOf(snapshot.Content) != snapshot.ContentHash)
        { throw new RuntimePlanDocumentException("内容哈希不符"); }
        if (snapshot.SchemaVersion == 1)
        { return (RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(snapshot.Content)), null); }
        if (snapshot.SchemaVersion != 2)
        { throw new RuntimePlanDocumentException($"快照的 schema 版本 {snapshot.SchemaVersion} 不受支持"); }
        try
        {
            var value = JsonSerializer.Deserialize<Envelope>(snapshot.Content, Json);
            if (value is not { SchemaVersion: 2, Draft: { Version.Length: > 0, Connections: not null, Devices: not null, Inputs: not null, Routes: not null } })
            { throw new RuntimePlanDocumentException("完整配置快照缺少草稿。"); }
            return (RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(value.Plan.GetRawText())), value.Draft);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw new RuntimePlanDocumentException("完整配置快照格式无效。"); }
    }

    private static void CheckSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > RuntimePlanDocument.MaximumBytes)
        { throw new RuntimePlanDocumentException("完整配置快照超过 16 MiB。"); }
    }

    private sealed record Envelope(int SchemaVersion, JsonElement Plan, ConfigurationCheckpoint Draft);
}

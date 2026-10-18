using SqlSugar;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Devices;

internal sealed record DeviceDeletionReview(string Version, long Id, string Key, string Name, int InputCount,
    bool CanDelete, DeviceRouteReference[] Routes, DeviceVersionReference[] Versions);
internal sealed record DeviceVersionReference(long Revision, string[] Positions, string[] Units);
internal sealed class DeviceRouteReference
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
}

internal sealed partial class DeviceDraftStore
{
    /// <summary>只读取引用元数据，不预留删除权；DELETE仍在原事务中重新检查版本与全部引用。
    /// 在短事务中捕获索引/指针/原始快照，解析冻结计划在事务之外，不执行模块或协议操作。</summary>
    public DeviceDeletionReview ReviewDeletion(long id, string expectedVersion)
    {
        if (id is < 1 or > 9007199254740991 || string.IsNullOrEmpty(expectedVersion) || expectedVersion.Length > 64)
        { throw new ArgumentException("设备编号或草稿版本无效。"); }
        var captured = database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, expectedVersion);
            var device = Read(client, id) ?? throw new DraftWriteException("device.notFound");
            var routes = client.Ado.SqlQuery<DeviceRouteReference>("""
                SELECT DISTINCT r.id AS Id, r.business_key AS Key, r.name AS Name, r.enabled AS Enabled
                FROM route_draft r JOIN route_draft_reference ref ON ref.route_id = r.id
                    JOIN input_draft i ON i.id = ref.target_id
                WHERE ref.kind = 'input' AND i.device_id = @id
                ORDER BY r.id
                """, new SugarParameter("@id", id)).ToArray();
            var inputs = client.Queryable<InputDraftRecord>().Where(input => input.DeviceId == id).Count();
            var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
            var pointers = new[] { (Position: "effective", Revision: state.EffectiveRevision),
                (Position: "previous", Revision: state.PreviousRevision), (Position: "pending", Revision: state.PendingRevision) }
                .Where(value => value.Revision is not null).ToArray();
            var snapshots = pointers.Select(value => value.Revision!.Value).Distinct().Select(revision =>
                client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                    ?? throw new RuntimePlanDocumentException("运行版本引用缺失。")).ToArray();
            return (device, routes, inputs, pointers, snapshots);
        });
        var versions = captured.snapshots.Select(snapshot =>
        {
            var plan = RuntimeSnapshotDocument.ReadPlan(snapshot);
            return new DeviceVersionReference(snapshot.Revision,
                captured.pointers.Where(pointer => pointer.Revision == snapshot.Revision).Select(pointer => pointer.Position).ToArray(),
                plan.Units.Where(unit => RefersToDevice(unit, captured.device.Key))
                    .Select(unit => unit.Id.Kind + "/" + unit.Id.Key).Distinct().Order(StringComparer.Ordinal).ToArray());
        }).Where(reference => reference.Units.Length > 0).ToArray();
        return new(expectedVersion, id, captured.device.Key, captured.device.Name, captured.inputs,
            captured.routes.Length == 0 && versions.Length == 0, captured.routes, versions);
    }
}

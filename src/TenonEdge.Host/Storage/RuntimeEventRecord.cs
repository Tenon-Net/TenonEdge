using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 运行事件（总设计 §3.3、§10.2 RuntimeEvent）：时间、级别、类型、对象、说明、合并次数。只记状态变化，
/// 说明中不写报文正文、点位的值和秘密。时间为 UTC Unix 毫秒；对象为空时保存空串，便于按“类型 + 对象”合并。
/// </summary>
[SugarTable(TableName)]
internal sealed class RuntimeEventRecord
{
    public const string TableName = "runtime_event";

    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "first_occurred_at")]
    public long FirstOccurredAt { get; set; }

    [SugarColumn(ColumnName = "last_occurred_at")]
    public long LastOccurredAt { get; set; }

    [SugarColumn(ColumnName = "level")]
    public string Level { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "type")]
    public string Type { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "target")]
    public string Target { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "message")]
    public string Message { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "occurrence_count")]
    public int OccurrenceCount { get; set; } = 1;
}

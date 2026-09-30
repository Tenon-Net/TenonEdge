using SqlSugar;

namespace TenonEdge.Host.Storage.Migrations;

/// <summary>
/// 迁移历史中的一条记录：已执行的迁移版本、名称、执行时间（UTC Unix 毫秒）与执行它的程序版本。
/// 配置库的 schema 版本（总设计 §10.2 SystemState 所列）即这张表中的最大版本号。
/// </summary>
[SugarTable(TableName)]
internal sealed class SchemaMigrationRecord
{
    public const string TableName = "schema_migration";

    [SugarColumn(ColumnName = "version", IsPrimaryKey = true)]
    public long Version { get; set; }

    [SugarColumn(ColumnName = "name")]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "applied_at")]
    public long AppliedAt { get; set; }

    [SugarColumn(ColumnName = "program_version")]
    public string ProgramVersion { get; set; } = string.Empty;
}

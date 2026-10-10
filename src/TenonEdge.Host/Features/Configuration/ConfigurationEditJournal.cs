using SqlSugar;

namespace TenonEdge.Host.Features.Configuration;

internal sealed record ConfigurationEdit(string Kind, long Id, string Stamp, string? UpdatedBy, long? UpdatedAt, bool Deleted);

/// <summary>每个配置对象只保留最后一次编辑；与草稿/审计共用调用方事务，不依赖审计保留期。</summary>
internal static class ConfigurationEditJournal
{
    public static void Record(SqlSugarClient client, string kind, long id, string? account, TimeProvider time, bool deleted = false)
    {
        client.Ado.ExecuteCommand("""
            INSERT INTO configuration_edit(kind, object_id, stamp, updated_by, updated_at, deleted, retained)
            VALUES (@kind, @id, @stamp, @account, @at, @deleted, @retained)
            ON CONFLICT(kind, object_id) DO UPDATE SET stamp = excluded.stamp, updated_by = excluded.updated_by,
                updated_at = excluded.updated_at, deleted = excluded.deleted
            """, new SugarParameter("@kind", kind), new SugarParameter("@id", id),
            new SugarParameter("@stamp", Guid.NewGuid().ToString("N")), new SugarParameter("@account", account),
            new SugarParameter("@at", time.GetUtcNow().ToUnixTimeMilliseconds()), new SugarParameter("@deleted", deleted),
            new SugarParameter("@retained", deleted));
        // 从未冻结/迁移的新增对象随后删除，不能产生永久墓碑；pending 冻结会先把 live 行标为 retained。
        if (deleted)
        {
            client.Ado.ExecuteCommand("UPDATE system_state SET id_floor = MAX(id_floor, @id) WHERE id = 1", new SugarParameter("@id", id));
            client.Ado.ExecuteCommand("DELETE FROM configuration_edit WHERE kind = @kind AND object_id = @id AND retained = 0",
                new SugarParameter("@kind", kind), new SugarParameter("@id", id));
        }
    }

    public static ConfigurationEdit[] Read(SqlSugarClient client) => client.Queryable<ConfigurationEditRecord>()
        .OrderBy(row => row.Kind).OrderBy(row => row.ObjectId).ToList()
        .Select(row => new ConfigurationEdit(row.Kind, row.ObjectId, row.Stamp, row.UpdatedBy, row.UpdatedAt, row.Deleted)).ToArray();

    public static void Retain(SqlSugarClient client, ConfigurationEdit[]? edits)
    {
        foreach (var group in (edits ?? []).Where(edit => !edit.Deleted).GroupBy(edit => edit.Kind))
        {
            foreach (var chunk in group.Select(edit => edit.Id).Chunk(100))
            {
                client.Updateable<ConfigurationEditRecord>().SetColumns(row => row.Retained == true)
                    .Where(row => row.Kind == group.Key && chunk.Contains(row.ObjectId)).ExecuteCommand();
            }
        }
    }

    public static void PruneCommitted(SqlSugarClient client, ConfigurationEdit[]? edits)
    {
        // 只清理本次已经生效的删除；冻结之后发生的新编辑/删除 stamp 不同，须留给下一次清单。
        foreach (var edit in (edits ?? []).Where(edit => edit.Deleted))
        {
            client.Deleteable<ConfigurationEditRecord>().Where(row => row.Kind == edit.Kind && row.ObjectId == edit.Id &&
                row.Stamp == edit.Stamp && row.Deleted).ExecuteCommand();
        }
    }

    public static void Restore(SqlSugarClient client, ConfigurationEdit[]? edits)
    {
        client.Ado.ExecuteCommand("UPDATE system_state SET id_floor = MAX(id_floor, (SELECT COALESCE(MAX(object_id), 0) FROM configuration_edit)) WHERE id = 1");
        client.Deleteable<ConfigurationEditRecord>().ExecuteCommand();
        var rows = (edits ?? []).Where(edit => !edit.Deleted).Select(edit => new ConfigurationEditRecord
        {
            Kind = edit.Kind,
            ObjectId = edit.Id,
            Stamp = edit.Stamp,
            UpdatedBy = edit.UpdatedBy,
            UpdatedAt = edit.UpdatedAt,
            Deleted = false,
            Retained = true,
        }).ToArray();
        foreach (var chunk in rows.Chunk(100))
        { client.Insertable(chunk).ExecuteCommand(); }
    }
}

[SugarTable("configuration_edit")]
internal sealed class ConfigurationEditRecord
{
    [SugarColumn(ColumnName = "kind", IsPrimaryKey = true)]
    public string Kind { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "object_id", IsPrimaryKey = true)]
    public long ObjectId { get; set; }
    [SugarColumn(ColumnName = "stamp")]
    public string Stamp { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_by", IsNullable = true)]
    public string? UpdatedBy { get; set; }
    [SugarColumn(ColumnName = "updated_at", IsNullable = true)]
    public long? UpdatedAt { get; set; }
    [SugarColumn(ColumnName = "deleted")]
    public bool Deleted { get; set; }
    [SugarColumn(ColumnName = "retained")]
    public bool Retained { get; set; }
}

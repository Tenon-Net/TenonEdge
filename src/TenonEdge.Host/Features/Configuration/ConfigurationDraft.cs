using SqlSugar;

namespace TenonEdge.Host.Features.Configuration;

/// <summary>全部配置对象共享同一草稿版本。只在调用方的一次配置库事务内检查与推进，不触及运行版本指针。</summary>
internal static class ConfigurationDraft
{
    public static string ReadVersion(SqlSugarClient client) => client.Queryable<ConfigurationDraftRecord>().InSingle(1)?.Version
        ?? throw new InvalidOperationException("配置库缺少草稿版本行。");

    public static void CheckVersion(SqlSugarClient client, string expected)
    {
        if (ReadVersion(client) != expected)
        { throw new DraftWriteException("configuration.draftConflict"); }
    }

    public static string Advance(SqlSugarClient client, TimeProvider time)
    {
        var version = Guid.NewGuid().ToString("N");
        client.Updateable(new ConfigurationDraftRecord { Version = version, UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds() }).ExecuteCommand();
        return version;
    }
}

internal sealed class DraftWriteException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
internal sealed record DraftVersion(string Version);

[SugarTable("configuration_draft")]
internal sealed class ConfigurationDraftRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    [SugarColumn(ColumnName = "version")]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_at")]
    public long UpdatedAt { get; set; }
}

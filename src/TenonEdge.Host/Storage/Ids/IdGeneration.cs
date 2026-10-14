using System.Globalization;
using System.Reflection;
using SqlSugar;

namespace TenonEdge.Host.Storage.Ids;

/// <summary>
/// 雪花 ID 生成器的装配（总设计 §10.1）：启动时以 config.db 中已有的最大 ID、SystemState 的 ID 下限
/// 与本版本的构建时间下限确定起点。使用雪花主键的表须登记在 <see cref="SnowflakeKeyedTables"/> 中，
/// 测试会核对库中的表与该清单一致，避免新增表后漏算已有的最大 ID。
/// </summary>
internal static class IdGeneration
{
    /// <summary>程序集元数据中构建时间下限的键，见 TenonEdge.Host.csproj。</summary>
    public const string BuildTimestampFloorMetadataKey = "TenonEdge.BuildTimestampFloor";

    /// <summary>以雪花 ID 作为主键（列名 id）的表。</summary>
    public static IReadOnlyList<string> SnowflakeKeyedTables { get; } = ["audit_log", "runtime_event", "user_account", "connection_draft", "device_draft", "input_draft", "route_draft", "api_token"];

    /// <summary>本程序集记录的构建时间下限。</summary>
    public static DateTimeOffset BuildTimestampFloor { get; } = ReadBuildTimestampFloor();

    public static SnowflakeIdGenerator CreateGenerator(ConfigDatabase database, TimeProvider time, ILogger logger) =>
        new(ReadFloor(database.Client), BuildTimestampFloor, time, logger);

    /// <summary>新 ID 必须大于的值：SystemState 的 ID 下限与各雪花主键表中最大 ID 的较大者。</summary>
    public static long ReadFloor(ISqlSugarClient client)
    {
        var floor = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)?.IdFloor ?? 0;
        foreach (var table in SnowflakeKeyedTables)
        {
            floor = Math.Max(floor, client.Ado.GetLong($"SELECT COALESCE(MAX(id), 0) FROM {table}"));
        }

        // 删除记录引用旧对象编号；它不分配自己的雪花 ID，但仍须阻止重启后复用。
        return Math.Max(floor, client.Ado.GetLong("SELECT COALESCE(MAX(object_id), 0) FROM configuration_edit"));
    }

    private static DateTimeOffset ReadBuildTimestampFloor()
    {
        var value = typeof(IdGeneration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == BuildTimestampFloorMetadataKey)?.Value;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var floor)
            ? floor
            : throw new InvalidOperationException($"程序集缺少有效的构建时间下限元数据 {BuildTimestampFloorMetadataKey}：“{value}”。");
    }
}

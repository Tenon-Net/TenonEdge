using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 实际加载的 SQLite 原生库版本与编译选项（总设计 §2.3，V-07）。持久化语义取决于实际加载的原生库，而不只是 NuGet 包版本，
/// 因此启动时从连接上读取并写入日志，同时记录 Microsoft.Data.Sqlite 与 SQLitePCLRaw 的程序集版本。
/// </summary>
internal sealed record SqliteRuntimeInfo(
    string Version,
    IReadOnlyList<string> CompileOptions,
    string MicrosoftDataSqliteVersion,
    string SqlitePclRawVersion)
{
    /// <summary>WAL 重置缺陷影响 3.7.0 至 3.51.2，在 3.51.3 修复（S-18）；低于此版本时拒绝启动运行时。</summary>
    public static Version MinimumVersion { get; } = new(3, 51, 3);

    public bool MeetsMinimumVersion => IsSupportedVersion(Version);

    /// <summary>无法解析的版本号按不满足下限处理。</summary>
    public static bool IsSupportedVersion(string version) =>
        System.Version.TryParse(version, out var parsed) && parsed >= MinimumVersion;

    public static SqliteRuntimeInfo Read(SqliteConnection connection)
    {
        string version;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sqlite_version();";
            version = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var compileOptions = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA compile_options;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                compileOptions.Add(reader.GetString(0));
            }
        }

        return new SqliteRuntimeInfo(
            version,
            compileOptions,
            InformationalVersionOf(typeof(SqliteConnection).Assembly),
            InformationalVersionOf(typeof(SQLitePCL.raw).Assembly));
    }

    private static string InformationalVersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}

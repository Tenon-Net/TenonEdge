using System.Globalization;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    private readonly IRuntimeEventSink? _events;
    private string RecoveryDirectory => FilePath + ".recovering";

    private void BeginRecovery()
    {
        ClearReadPool();
        var count = TryCountDamagedRecords();
        if (OperatingSystem.IsWindows())
        { Directory.CreateDirectory(RecoveryDirectory); }
        else
        { Directory.CreateDirectory(RecoveryDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        File.WriteAllText(Path.Combine(RecoveryDirectory, "count.txt"), count?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
    }

    // 固定暂存目录是恢复标记。主库先移走；重启时先收齐侧文件，不能让旧 WAL 附到新空库。
    // 中途移动失败就停止初始化，不删旧库、不绕过错误创建新库。
    private void FinishInterruptedRecovery()
    {
        if (!Directory.Exists(RecoveryDirectory))
        { return; }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = FilePath + suffix;
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(RecoveryDirectory, DeliveryCacheOptions.FileName + suffix));
            }
        }

        var countFile = Path.Combine(RecoveryDirectory, "count.txt");
        var count = File.Exists(countFile) ? File.ReadAllText(countFile) : "unknown";
        var description = long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? $"旧库可读取的记录数：{value}。" : "旧库记录数量未知。";
        _events?.Record(RuntimeEventSeverity.Error, "storage.queueRebuilt", DeliveryCacheOptions.FileName,
            $"发送缓存损坏，旧库及侧文件移到旁边，将尝试新建空库；新库累计计数从零开始。{description}");
        Directory.Move(RecoveryDirectory, FilePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
    }

    private long? TryCountDamagedRecords()
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = FilePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            return Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM delivery NOT INDEXED;"), CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { return null; }
    }
}

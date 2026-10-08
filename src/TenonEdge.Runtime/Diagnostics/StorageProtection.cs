namespace TenonEdge.Runtime.Diagnostics;

/// <summary>磁盘实际可用空间，不以 payload 配额代替文件系统余量。</summary>
public sealed record StorageSpace(long TotalBytes, long AvailableBytes);

/// <summary>无法读取空间时也暂停新增入队；删除和已提交记录的发送不受影响。</summary>
public sealed record StorageProtectionStatus(bool RejectNewRecords, bool SpaceUnknown, StorageSpace? Space);

public sealed class StorageProtection
{
    private readonly Func<StorageSpace?> _read;
    private readonly long _minimumBytes;
    private readonly double _minimumFraction;
    private StorageProtectionStatus _status = new(true, true, null);

    public StorageProtection(string directory, long minimumBytes, double minimumFraction, Func<StorageSpace?>? read = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBytes);
        if (!double.IsFinite(minimumFraction) || minimumFraction is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFraction));
        }

        _minimumBytes = minimumBytes;
        _minimumFraction = minimumFraction;
        _read = read ?? (() => ReadSpace(directory));
    }

    public StorageProtectionStatus Status => Volatile.Read(ref _status);

    internal StorageProtectionStatus Refresh()
    {
        var space = _read();
        if (space is { TotalBytes: <= 0 } or { AvailableBytes: < 0 } || space?.AvailableBytes > space?.TotalBytes)
        {
            space = null;
        }

        var status = new StorageProtectionStatus(space is null || space.AvailableBytes < _minimumBytes
            || space.AvailableBytes < space.TotalBytes * _minimumFraction, space is null, space);
        Volatile.Write(ref _status, status);
        return status;
    }

    private static StorageSpace? ReadSpace(string directory)
    {
        try
        {
            var drive = new DriveInfo(directory);
            return new(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

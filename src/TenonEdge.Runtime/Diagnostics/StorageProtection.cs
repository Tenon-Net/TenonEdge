namespace TenonEdge.Runtime.Diagnostics;

/// <summary>磁盘实际可用空间，不以 payload 配额代替文件系统余量。</summary>
public sealed record StorageSpace(long TotalBytes, long AvailableBytes, string? FileSystem = null);

/// <summary>无法读取空间时也暂停新增入队；删除和已提交记录的发送不受影响。</summary>
public sealed record StorageProtectionStatus(bool RejectNewRecords, bool SpaceUnknown, StorageSpace? Space);

public sealed class StorageProtection
{
    private readonly Func<StorageSpace?> _read;
    private readonly Func<(long Bytes, double Fraction)> _readLimits;
    private StorageProtectionStatus _status = new(true, true, null);

    public StorageProtection(string directory, long minimumBytes, double minimumFraction, Func<StorageSpace?>? read = null, Func<(long Bytes, double Fraction)>? readLimits = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBytes);
        if (!double.IsFinite(minimumFraction) || minimumFraction is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFraction));
        }

        _readLimits = readLimits ?? (() => (minimumBytes, minimumFraction));
        _read = read ?? (() => ReadSpace(directory));
    }

    public StorageProtectionStatus Status => Evaluate(Volatile.Read(ref _status).Space);

    internal StorageProtectionStatus Refresh()
    {
        var space = _read();
        if (space is { TotalBytes: <= 0 } or { AvailableBytes: < 0 } || space?.AvailableBytes > space?.TotalBytes)
        {
            space = null;
        }

        var status = Evaluate(space);
        Volatile.Write(ref _status, status);
        return status;
    }

    private StorageProtectionStatus Evaluate(StorageSpace? space)
    {
        var limits = _readLimits();
        ArgumentOutOfRangeException.ThrowIfNegative(limits.Bytes);
        if (!double.IsFinite(limits.Fraction) || limits.Fraction is < 0 or >= 1)
        { throw new InvalidOperationException("磁盘保护参数提供者返回了无效百分比。"); }
        return new(space is null || space.AvailableBytes < limits.Bytes
            || space.AvailableBytes < space.TotalBytes * limits.Fraction, space is null, space);
    }

    private static StorageSpace? ReadSpace(string directory)
    {
        try
        {
            var drive = new DriveInfo(directory);
            var space = new StorageSpace(drive.TotalSize, drive.AvailableFreeSpace);
            // Calibration metadata must not invalidate otherwise readable free space.
            try
            { return space with { FileSystem = drive.DriveFormat }; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { return space; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

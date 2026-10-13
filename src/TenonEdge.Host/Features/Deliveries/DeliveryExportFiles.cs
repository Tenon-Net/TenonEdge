using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Deliveries;

/// <summary>仅此功能拥有的单个暂存文件；不把文件路径或目录列举能力交给请求方。</summary>
internal sealed class DeliveryExportFiles
{
    internal const string DirectoryName = "delivery-export";
    internal const string FileName = "snapshot.jsonl.tmp";
    private readonly string _directory;
    private readonly string _path;
    private readonly DeliveryCacheOptions _options;
    private readonly Func<StorageSpace?> _readSpace;

    public DeliveryExportFiles(DataDirectoryLock dataLock, DeliveryCache cache, Func<StorageSpace?>? readSpace = null)
    {
        _directory = Path.Combine(dataLock.Directory.Root, DirectoryName);
        _path = Path.Combine(_directory, FileName);
        _options = cache.Options;
        _readSpace = readSpace ?? (() =>
        {
            try
            {
                var drive = new DriveInfo(_directory);
                return new(drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { return null; }
        });
    }

    public void Initialize()
    {
        RejectLink(_directory);
        PrivateFileSystem.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        { File.SetUnixFileMode(_directory, PrivateFileSystem.DirectoryMode); }
        // 先取得整个数据目录锁，启动时才删除上次强杀留下的这一特定文件。不遍历、不删除未知文件。
        Delete();
    }

    public FileStream Create(long maximumBytes)
    {
        RejectLink(_directory);
        RejectLink(_path);
        CheckSpace(maximumBytes);
        var options = PrivateFileSystem.Options(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        options.Options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        return new FileStream(_path, options);
    }

    public void Delete()
    {
        RejectLink(_directory);
        RejectLink(_path);
        File.Delete(_path);
    }

    public Stream ProtectWrites(FileStream file) => new SpaceCheckedStream(file, CheckSpace);

    private void CheckSpace(long requiredBytes)
    {
        var space = _readSpace();
        if (space is null || space.TotalBytes <= 0 || space.AvailableBytes < 0 || space.AvailableBytes > space.TotalBytes)
        { throw new DeliveryExportSpaceException(); }
        var reserve = Math.Max(_options.MinimumDiskFreeBytes, (long)Math.Ceiling(space.TotalBytes * _options.MinimumDiskFreeFraction));
        if (space.AvailableBytes < reserve || requiredBytes > space.AvailableBytes - reserve)
        { throw new DeliveryExportSpaceException(); }
    }

    private static void RejectLink(string path)
    {
        // LinkTarget also catches dangling symlinks, which File.Exists deliberately reports as absent.
        if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new IOException("导出暂存路径不能是链接。"); }
    }

    // 每次实际写入前重新读磁盘余量，不借用队列的旧监控值；仍须处理并发用盘导致的真正IO失败。
    // 文件由功能所有者关闭，此包装不拥有底层句柄。
    private sealed class SpaceCheckedStream(FileStream file, Action<long> check) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => file.Position = value; }
        public override void Flush() => file.Flush();
        public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        { check(count); file.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { check(buffer.Length); await file.WriteAsync(buffer, cancellationToken); }
    }
}

internal sealed class DeliveryExportSpaceException : IOException;

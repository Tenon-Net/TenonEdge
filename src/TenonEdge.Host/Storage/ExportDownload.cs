namespace TenonEdge.Host.Storage;

/// <summary>请求范围的唯一下载租约；取消/失败同样释放文件与管理槽，不持有数据库事务。</summary>
internal sealed class ExportDownload(FileStream file, Action<bool> finish, CancellationToken lifetime) : IDisposable
{
    private int _disposed;
    private bool _success;
    public long Length => file.Length;
    public async Task CopyToAsync(Stream destination, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token);
        await file.CopyToAsync(destination, 65536, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        _success = true;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        { finish(_success); }
    }
}

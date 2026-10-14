using System.Globalization;
using System.Text;
using SqlSugar;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Audit;

/// <summary>
/// 冻结审计编号上界，并在生成期间暂缓过期删除。生产审计的编号在原写事务中分配，因此之后提交的记录只能在上界之后。
/// 每页独立短连接；写文件/等待流时没有活跃reader或事务。由有期限的导出所有者持有并释放，下载前必须释放。
/// </summary>
internal sealed class AuditCsvSnapshot : IDisposable
{
    internal const int PageSize = 200;
    private readonly ConfigDatabase _database;
    private readonly AuditFilter _filter;
    private readonly IDisposable _retention;
    private int _used;
    private int _disposed;
    private AuditCsvSnapshot(ConfigDatabase database, AuditFilter filter, IDisposable retention, long upperId)
    { _database = database; _filter = filter; _retention = retention; UpperId = upperId; }
    public long UpperId { get; }

    public static AuditCsvSnapshot Capture(ConfigDatabase database, AuditRetentionGate gate, AuditFilter filter)
    {
        filter.Validate();
        var retention = gate.TryEnterSnapshot() ?? throw new AuditSnapshotBusyException();
        try
        {
            // 取得原配置写锁，使之前的审计写入先完成；只读MAX(id)，立即结束此短事务。
            var upper = database.InTransaction(client => client.Ado.GetLong("SELECT COALESCE(MAX(id), 0) FROM audit_log"));
            return new(database, filter, retention, upper);
        }
        catch { retention.Dispose(); throw; }
    }

    public async Task<long> WriteAsync(Stream output, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _used, 1) != 0)
        { throw new InvalidOperationException("审计快照只能生成一次。"); }
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 16 * 1024, leaveOpen: true)
        { NewLine = "\r\n" };
        await writer.WriteLineAsync("id,occurredAt,accountName,tokenName,sourceIp,action,target,result,detail".AsMemory(), token);
        long before = UpperId + 1;
        long count = 0;
        while (before > 1)
        {
            token.ThrowIfCancellationRequested();
            List<AuditLogRecord> page;
            using (var client = _database.CreateIsolatedClient())
            { page = _filter.Apply(client).Where(row => row.Id <= UpperId && row.Id < before).OrderBy(row => row.Id, OrderByType.Desc).Take(PageSize).ToList(); }
            foreach (var row in page)
            {
                token.ThrowIfCancellationRequested();
                string?[] cells = [row.Id.ToString(CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeMilliseconds(row.OccurredAt).ToString("O", CultureInfo.InvariantCulture),
                    row.AccountName, row.TokenName, row.SourceIp, row.Action, row.Target, row.Result, row.Detail];
                await writer.WriteLineAsync(string.Join(',', cells.Select(Quote)).AsMemory(), token);
                count++;
            }
            if (page.Count < PageSize)
            { break; }
            before = page[^1].Id;
        }
        await writer.FlushAsync(token);
        return count;
    }
    private static string Quote(string? value)
    {
        value ??= "";
        var significant = value.AsSpan().TrimStart();
        // RFC4180引号不防表格公式注入；对危险开头加文本前缀，保留其余原文和换行。
        var unsafeCell = value.Length > 0 && (value[0] is '\t' or '\r' or '\n' ||
            !significant.IsEmpty && significant[0] is '=' or '+' or '-' or '@');
        return "\"" + (unsafeCell ? "'" : "") + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _retention.Dispose(); } }
}
internal sealed class AuditSnapshotBusyException : Exception;

using System.Globalization;
using System.Text.Json;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Audit;

/// <summary>单网关单份有期限的审计导出。只拥有任务、原始身份与文件，不捕获请求、作用域服务或数据库事务。</summary>
internal sealed class AuditExportManager : IAsyncDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private readonly ConfigDatabase _database;
    private readonly AuditRetentionGate _retention;
    private readonly AuditExportFiles _files;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private Export? _current;
    private bool _available;
    private bool _stopping;

    public AuditExportManager(ConfigDatabase database, AuditRetentionGate retention, AuditExportFiles files, AuditTrail audit,
        TimeProvider time, IHostApplicationLifetime lifetime)
    {
        _database = database;
        _retention = retention;
        _files = files;
        _audit = audit;
        _time = time;
        try
        { files.Initialize(); _available = true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _available = false; }
        _stoppingRegistration = lifetime.ApplicationStopping.Register(Stop);
    }

    public AuditExportStatus Start(AuditExportActor actor, AuditFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Validate();
        lock (_gate)
        {
            if (_stopping || !_available)
            { throw Error(503, "audit.exportUnavailable"); }
            if (_current is { } previous)
            {
                if (Busy(previous))
                { throw Error(409, "audit.exportBusy"); }
                previous.Timer?.Dispose();
                previous.Cancellation.Dispose();
            }
            var export = new Export(actor, filter, _time.GetUtcNow() + Lifetime);
            _current = export;
            export.Timer = _time.CreateTimer(_ => CancelExport(export, AuditExportState.Expired), null, Lifetime, Timeout.InfiniteTimeSpan);
            using (ExecutionContext.SuppressFlow())
            { export.Work = Task.Run(() => GenerateAsync(export)); }
            return Status(export);
        }
    }

    public AuditExportStatus? GetCurrent(string owner)
    { lock (_gate) { return _current is { } export && export.Actor.Owner == owner ? Status(export) : null; } }

    public AuditExportStatus Cancel(Guid id, string owner)
    {
        lock (_gate)
        {
            var export = Owned(id, owner);
            CancelExport(export, AuditExportState.Cancelled);
            return Status(export);
        }
    }

    public ExportDownload OpenDownload(Guid id, string owner)
    {
        lock (_gate)
        {
            var export = Owned(id, owner);
            if (_stopping || export.State != AuditExportState.Ready || export.File is null)
            { throw Error(409, "audit.exportNotReady"); }
            export.State = AuditExportState.Downloading;
            export.File.Position = 0;
            export.Download = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(export.File, success => FinishDownload(export, success), export.Cancellation.Token);
        }
    }

    private async Task GenerateAsync(Export export)
    {
        FileStream? file = null;
        var generated = false;
        try
        {
            var token = export.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            long count;
            long upperId;
            using (var snapshot = AuditCsvSnapshot.Capture(_database, _retention, export.Filter))
            {
                token.ThrowIfCancellationRequested();
                upperId = snapshot.UpperId;
                file = _files.Create(0);
                using var guarded = _files.ProtectWrites(file);
                count = await snapshot.WriteAsync(guarded, token);
            } // 文件已完整生成，下载与写导出审计前释放保留租约；从未跨文件IO持有数据库事务。
            await file.FlushAsync(token);
            token.ThrowIfCancellationRequested();
            var entry = new AuditEntry("audit.export", AuditResults.Success, export.Actor.Account, export.Actor.SourceIp,
                export.Id.ToString("D"), JsonSerializer.Serialize(new { stage = "generated", export.Filter, upperId, count, bytes = file.Length }));
            // 导出自身审计必须成功提交才可下载；不能把日志降级当作审计已持久化。
            _database.InTransaction(client => { token.ThrowIfCancellationRequested(); _audit.Append(client, entry); return true; });
            generated = true;
            lock (_gate)
            {
                if (token.IsCancellationRequested)
                { return; }
                export.Count = count;
                export.UpperId = upperId;
                export.FileBytes = file.Length;
                export.File = file;
                file = null;
                export.State = AuditExportState.Ready;
            }
        }
        catch (OperationCanceledException) when (export.Cancellation.IsCancellationRequested) { }
        catch (AuditSnapshotBusyException) { Fail(export, "audit.exportRetentionBusy"); }
        catch (ExportSpaceException) { Fail(export, "audit.exportInsufficientSpace"); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(export, "audit.exportGenerationFailed"); }
        finally
        {
            if (file is not null)
            {
                try
                { await file.DisposeAsync(); }
                catch (IOException) { CleanupFailed(export); }
                finally { DeleteFile(export); }
            }
            else if (!generated)
            { DeleteFile(export); }
            if (!generated)
            { WriteOutcome(export, "audit.export", false, export.Error ?? "cancelledOrExpired"); }
        }
    }

    private void FinishDownload(Export export, bool success)
    {
        lock (_gate)
        {
            if (export.State == AuditExportState.Downloading)
            {
                export.State = success ? AuditExportState.ResponseWritten : AuditExportState.Failed;
                if (!success)
                { export.Error = "audit.exportDownloadFailed"; }
            }
            CloseFile(export);
        }
        try
        { WriteOutcome(export, "audit.export.download", success, "httpResponse"); }
        finally { export.Download!.TrySetResult(); }
    }

    private void Fail(Export export, string code)
    {
        lock (_gate)
        {
            if (export.Cancellation.IsCancellationRequested)
            { return; }
            export.Error = code;
            export.State = AuditExportState.Failed;
        }
    }
    private void CancelExport(Export export, AuditExportState state)
    {
        lock (_gate)
        {
            if (Terminal(export.State))
            { return; }
            var downloading = export.State == AuditExportState.Downloading;
            export.State = state;
            export.Cancellation.Cancel();
            if (!downloading)
            { CloseFile(export); }
        }
    }
    private void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_current is { } export)
            { CancelExport(export, AuditExportState.Cancelled); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        _stoppingRegistration.Dispose();
        Export? export;
        lock (_gate)
        { export = _current; }
        if (export is null)
        { return; }
        await export.Work;
        if (export.Download is not null)
        { await export.Download.Task; }
        export.Timer?.Dispose();
        export.Cancellation.Dispose();
    }
    private void CloseFile(Export export)
    {
        if (export.File is not { } file)
        { return; }
        export.File = null;
        try
        { file.Dispose(); }
        catch (IOException) { CleanupFailed(export); }
        finally { DeleteFile(export); }
    }
    private void DeleteFile(Export export)
    {
        try
        { _files.Delete(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { CleanupFailed(export); }
    }
    private void CleanupFailed(Export export)
    { lock (_gate) { _available = false; export.Error = "audit.exportCleanupFailed"; } }
    private void WriteOutcome(Export export, string action, bool success, string stage) =>
        _audit.AppendOrLog(new(action, success ? AuditResults.Success : AuditResults.Failure, export.Actor.Account,
            export.Actor.SourceIp, export.Id.ToString("D"), JsonSerializer.Serialize(new { stage, export.Filter, export.Count, export.FileBytes })));
    private Export Owned(Guid id, string owner) => _current is { } export && export.Id == id && export.Actor.Owner == owner
        ? export : throw Error(404, "audit.exportNotFound");
    private static AuditExportRequestException Error(int status, string code) => new(status, code);
    private static bool Terminal(AuditExportState state) => state is AuditExportState.ResponseWritten or AuditExportState.Failed or AuditExportState.Cancelled or AuditExportState.Expired;
    private static bool Busy(Export export) => !Terminal(export.State) || !export.Work.IsCompleted || export.Download is { Task.IsCompleted: false };
    private static AuditExportStatus Status(Export export) => new(export.Id, export.State, Busy(export), export.ExpiresAt,
        export.Filter, Text(export.Count), Text(export.FileBytes), export.UpperId, export.Error);
    private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private sealed class Export(AuditExportActor actor, AuditFilter filter, DateTimeOffset expiresAt)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AuditExportActor Actor { get; } = actor;
        public AuditFilter Filter { get; } = filter;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public CancellationTokenSource Cancellation { get; } = new();
        public AuditExportState State { get; set; } = AuditExportState.Generating;
        public Task Work { get; set; } = Task.CompletedTask;
        public TaskCompletionSource? Download { get; set; }
        public ITimer? Timer { get; set; }
        public FileStream? File { get; set; }
        public long? FileBytes { get; set; }
        public long? Count { get; set; }
        public long? UpperId { get; set; }
        public string? Error { get; set; }
    }
}
internal enum AuditExportState { Generating, Ready, Downloading, ResponseWritten, Failed, Cancelled, Expired }
internal sealed record AuditExportActor(string Owner, AuditAccount Account, string? SourceIp);
internal sealed record AuditExportStatus(Guid Id, AuditExportState State, bool Busy, DateTimeOffset ExpiresAt,
    AuditFilter Filter, string? Count, string? FileBytes, long? UpperId, string? Error);
internal sealed class AuditExportRequestException(int statusCode, string code) : Exception
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

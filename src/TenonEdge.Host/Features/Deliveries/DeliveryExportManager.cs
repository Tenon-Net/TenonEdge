using System.Globalization;
using System.Text.Json;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

/// <summary>一台网关最多一份临时导出。只保存原始值与自有任务/文件，不持有请求、作用域服务或数据库事务。</summary>
internal sealed class DeliveryExportManager : IAsyncDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private readonly DeliveryCache _cache;
    private readonly DeliveryExportFiles _files;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private Export? _current;
    private bool _available;
    private bool _stopping;

    public DeliveryExportManager(DeliveryCache cache, DeliveryExportFiles files, AuditTrail audit, TimeProvider time,
        IHostApplicationLifetime lifetime)
    {
        _cache = cache;
        _files = files;
        _audit = audit;
        _time = time;
        try
        { files.Initialize(); _available = true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _available = false; }
        _stoppingRegistration = lifetime.ApplicationStopping.Register(Stop);
    }

    public DeliveryExportStatus Prepare(DeliveryExportActor actor, DeliveryExportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Validate();
        lock (_gate)
        {
            if (_stopping || !_available)
            { throw Error(503, "delivery.exportUnavailable"); }
            if (_current is { } previous)
            {
                if (Busy(previous))
                { throw Error(409, "delivery.exportBusy"); }
                previous.Timer?.Dispose();
                previous.Cancellation.Dispose();
            }
            var export = new Export(actor, filter, _time.GetUtcNow() + Lifetime);
            _current = export;
            export.Timer = _time.CreateTimer(_ => CancelExport(export, DeliveryExportState.Expired), null, Lifetime, Timeout.InfiniteTimeSpan);
            using (ExecutionContext.SuppressFlow())
            { export.Work = Task.Run(() => Review(export)); }
            return Status(export);
        }
    }

    public DeliveryExportStatus? GetCurrent(string owner)
    {
        lock (_gate)
        { return _current is { } export && export.Actor.Owner == owner ? Status(export) : null; }
    }

    public DeliveryExportStatus Confirm(Guid id, string owner)
    {
        lock (_gate)
        {
            var export = Owned(id, owner);
            if (_stopping || export.State != DeliveryExportState.AwaitingConfirmation || export.Review is not { Count: > 0 })
            { throw Error(409, "delivery.exportNotConfirmable"); }
            export.State = DeliveryExportState.Generating;
            using (ExecutionContext.SuppressFlow())
            { export.Work = Task.Run(() => GenerateAsync(export)); }
            return Status(export);
        }
    }

    public DeliveryExportStatus Cancel(Guid id, string owner)
    {
        lock (_gate)
        {
            var export = Owned(id, owner);
            CancelExport(export, DeliveryExportState.Cancelled);
            return Status(export);
        }
    }

    public DeliveryExportDownload OpenDownload(Guid id, string owner)
    {
        lock (_gate)
        {
            var export = Owned(id, owner);
            if (_stopping || export.State != DeliveryExportState.Ready || export.File is null)
            { throw Error(409, "delivery.exportNotReady"); }
            export.State = DeliveryExportState.Downloading;
            export.File.Position = 0;
            export.Download = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(export.File, success => FinishDownload(export, success), export.Cancellation.Token);
        }
    }

    private void Review(Export export)
    {
        try
        {
            var review = _cache.ReviewExport(export.Filter, export.Cancellation.Token);
            lock (_gate)
            {
                if (export.Cancellation.IsCancellationRequested)
                { return; }
                export.Review = review;
                export.State = DeliveryExportState.AwaitingConfirmation;
            }
        }
        catch (OperationCanceledException) when (export.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(export, "delivery.exportReviewFailed"); }
    }

    private async Task GenerateAsync(Export export)
    {
        FileStream? file = null;
        var generated = false;
        try
        {
            var review = export.Review!;
            export.Cancellation.Token.ThrowIfCancellationRequested();
            file = _files.Create(review.MaximumFileBytes);
            using (var guarded = _files.ProtectWrites(file))
            {
                if (!await _cache.WriteExportSpoolAsync(review, guarded, export.Cancellation.Token))
                { Fail(export, "delivery.exportSelectionChanged"); return; }
            }
            await file.FlushAsync(export.Cancellation.Token);
            export.Cancellation.Token.ThrowIfCancellationRequested();
            // 先审计“文件生成”再允许下载。不是用户已保存，也不是跨库队列迁移审计。
            WriteAudit(export, "delivery.export", AuditResults.Success, "generated", file.Length);
            generated = true;
            lock (_gate)
            {
                if (export.Cancellation.IsCancellationRequested)
                { return; }
                export.File = file;
                export.FileBytes = file.Length;
                file = null;
                export.State = DeliveryExportState.Ready;
            }
        }
        catch (OperationCanceledException) when (export.Cancellation.IsCancellationRequested) { }
        catch (DeliveryExportSpaceException) { Fail(export, "delivery.exportInsufficientSpace"); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(export, "delivery.exportGenerationFailed"); }
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
            { DeleteFile(export); } // 构造文件流失败也可能留下已创建的空路径。
            if (!generated)
            { WriteAudit(export, "delivery.export", AuditResults.Failure, export.Error ?? "cancelledOrExpired", null); }
        }
    }

    private void FinishDownload(Export export, bool success)
    {
        lock (_gate)
        {
            // 成功仅表示 HTTP 写出完成，不推断浏览器保存成功。
            if (export.State == DeliveryExportState.Downloading)
            {
                export.State = success ? DeliveryExportState.ResponseWritten : DeliveryExportState.Failed;
                if (!success)
                { export.Error = "delivery.exportDownloadFailed"; }
            }
            CloseFile(export);
        }
        try
        { WriteAudit(export, "delivery.export.download", success ? AuditResults.Success : AuditResults.Failure, "httpResponse", export.FileBytes); }
        finally { export.Download!.TrySetResult(); }
    }

    private void Fail(Export export, string code)
    {
        lock (_gate)
        {
            if (export.Cancellation.IsCancellationRequested)
            { return; }
            export.Error = code;
            export.State = DeliveryExportState.Failed;
        }
    }

    private void CancelExport(Export export, DeliveryExportState state)
    {
        lock (_gate)
        {
            if (Terminal(export.State))
            { return; }
            var downloading = export.State == DeliveryExportState.Downloading;
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
            { CancelExport(export, DeliveryExportState.Cancelled); }
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
        if (export.File is null)
        { return; }
        var file = export.File;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CleanupFailed(export); }
    }

    private void CleanupFailed(Export export)
    {
        lock (_gate)
        {
            _available = false;
            export.Error = "delivery.exportCleanupFailed";
        }
    }

    private void WriteAudit(Export export, string action, string result, string stage, long? bytes) =>
        _audit.AppendOrLog(new(action, result, export.Actor.Account, export.Actor.SourceIp, export.Id.ToString("D"),
            JsonSerializer.Serialize(new { stage, state = export.Filter.State.ToString(), export.Filter.ConnectionId, export.Filter.From, export.Filter.To, count = export.Review?.Count, bytes, cleanupFailed = export.Error == "delivery.exportCleanupFailed" })));

    private Export Owned(Guid id, string owner) => _current is { } export && export.Id == id && export.Actor.Owner == owner
        ? export : throw Error(404, "delivery.exportNotFound");
    private static DeliveryExportRequestException Error(int status, string code) => new(status, code);
    private static bool Terminal(DeliveryExportState state) => state is DeliveryExportState.ResponseWritten or
        DeliveryExportState.Failed or DeliveryExportState.Cancelled or DeliveryExportState.Expired;
    private static bool Busy(Export export) => !Terminal(export.State) || !export.Work.IsCompleted || export.Download is { Task.IsCompleted: false };
    private static DeliveryExportStatus Status(Export export) => new(export.Id, export.State, Busy(export), export.ExpiresAt,
        export.Review is { } review ? new(review.Filter, Text(review.Count), Text(review.PayloadBytes), Text(review.MaximumFileBytes),
            review.FirstCreatedAt, review.LastCreatedAt) : null, export.FileBytes is { } bytes ? Text(bytes) : null, export.Error);
    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class Export(DeliveryExportActor actor, DeliveryExportFilter filter, DateTimeOffset expiresAt)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DeliveryExportActor Actor { get; } = actor;
        public DeliveryExportFilter Filter { get; } = filter;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public CancellationTokenSource Cancellation { get; } = new();
        public DeliveryExportState State { get; set; } = DeliveryExportState.Preparing;
        public DeliveryExportReview? Review { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
        public TaskCompletionSource? Download { get; set; }
        public ITimer? Timer { get; set; }
        public FileStream? File { get; set; }
        public long? FileBytes { get; set; }
        public string? Error { get; set; }
    }
}

internal enum DeliveryExportState { Preparing, AwaitingConfirmation, Generating, Ready, Downloading, ResponseWritten, Failed, Cancelled, Expired }
internal sealed record DeliveryExportActor(string Owner, AuditAccount Account, string? SourceIp);
internal sealed record DeliveryExportSummary(DeliveryExportFilter Filter, string Count, string PayloadBytes, string MaximumFileBytes,
    DateTimeOffset? FirstCreatedAt, DateTimeOffset? LastCreatedAt);
internal sealed record DeliveryExportStatus(Guid Id, DeliveryExportState State, bool Busy, DateTimeOffset ExpiresAt,
    DeliveryExportSummary? Review, string? FileBytes, string? Error);
internal sealed class DeliveryExportRequestException(int statusCode, string code) : Exception
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>请求范围的唯一下载租约；取消/失败同样释放文件与管理槽，不持有数据库事务。</summary>
internal sealed class DeliveryExportDownload(FileStream file, Action<bool> finish, CancellationToken lifetime) : IDisposable
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

using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Http;

public sealed class HttpInputFactory(IHttpCredentialResolver credentials, IOutboundConnector connector,
    ISharedConnections connections, IJsonInputDecoderFactory decoders, ITlsTrustResolver? trust = null) : IInputUnitFactory
{
    public string Kind => "input.http";
    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
    {
        try
        {
            var settings = HttpInputSettings.Parse(unit.Configuration);
            return new HttpInputUnit(unit.Id, settings, context, decoders.Build(settings.Fields, settings.UpstreamTime), credentials, connector, connections, trust);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { throw new RuntimeUnitBuildException(unit.Id, "HTTP 输入配置无效，请核对连接、周期和字段定义。", exception); }
    }
}

internal sealed class HttpInputUnit(RuntimeUnitId id, HttpInputSettings settings, InputUnitContext context, IJsonInputDecoder decoder,
    IHttpCredentialResolver credentials, IOutboundConnector connector, ISharedConnections connections, ITlsTrustResolver? trust = null) : IRuntimeUnit
{
    // 覆盖 1 MiB 字节、JSON 元数据池扩容、4 MiB 字段结果及构造临时值；先预约再读取，不另建队列。
    internal const long ReservationBytes = 48L * 1024 * 1024;
    public RuntimeUnitId Id => id;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        { return; }
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        await using var lease = connections.Acquire(settings.ConnectionId, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs, trust));
        var period = TimeSpan.FromMilliseconds(settings.PeriodMs);
        var started = context.Time.GetTimestamp();
        long cycle = 0;
        try
        {
            while (true)
            {
                // 固定周期；处理/通道背压超过周期就跳过，不叠加请求、不靠 Task.Run 掩盖阻塞。
                var cycleRemaining = period * (cycle + 1) - context.Time.GetElapsedTime(started);
                if (cycleRemaining <= TimeSpan.Zero)
                { context.Reads.CycleSkipped(context.Time.GetUtcNow()); cycle++; continue; }
                using var deadline = new CancellationTokenSource(cycleRemaining, context.Time);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                try
                {
                    await context.Messages.ProduceAsync(ReservationBytes, 4 * 1024 * 1024 + 8192 + RawJsonPayload.MaximumBudgetBytes, async token =>
                    {
                        using var requestDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), context.Time);
                        using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(token, requestDeadline.Token);
                        try
                        {
                            var captured = await lease.ExecuteAsync((session, cancel) => session.ReadDiagnosticInputAsync(settings, credentials, cancel), requestToken.Token);
                            using var document = captured.Document;
                            var receivedAt = context.Time.GetUtcNow();
                            var fields = decoder.Decode(document.RootElement, receivedAt, context.ClockState, requestToken.Token);
                            requestToken.Token.ThrowIfCancellationRequested();
                            var message = new EdgeMessage(Guid.CreateVersion7(receivedAt), context.InputKey, context.DeviceKey,
                                fields.CollectedAt, fields.TimeSource, receivedAt, fields.Values, fields.Quality, RawJsonPayload.CopyFrom(document.RootElement), captured.Access, new(fields.InvalidUpstreamTime));
                            if (fields.InvalidUpstreamTime)
                            { context.Reads.UpstreamTimeInvalid(); }
                            context.Reads.ReadCompleted(receivedAt, fields.Quality.Count);
                            return message;
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            context.Reads.ReadFailed(context.Time.GetUtcNow(), "HTTP 拉取排队或网络超时。");
                            return null;
                        }
                    }, operation.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                { context.Reads.CycleSkipped(context.Time.GetUtcNow()); }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
                {
                    var dataError = exception is HttpInputResponseException { Failure: not HttpInputFailure.Status } or InputMessageSizeException;
                    context.Reads.ReadFailed(context.Time.GetUtcNow(), dataError ? "HTTP 输入正文或字段无效/超限。" : "HTTP 拉取失败，请检查连接、出站策略、TLS 或凭证。",
                        dataError ? InputReadFailureKind.DataError : InputReadFailureKind.ReadError);
                }
                cycle++;
                var elapsed = context.Time.GetElapsedTime(started);
                var due = (long)(elapsed / period);
                for (; cycle < due; cycle++)
                { context.Reads.CycleSkipped(context.Time.GetUtcNow()); }
                var remaining = period * cycle - context.Time.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                { await Task.Delay(remaining, context.Time, stoppingToken); }
                stoppingToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

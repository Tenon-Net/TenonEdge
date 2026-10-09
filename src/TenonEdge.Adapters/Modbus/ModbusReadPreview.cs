using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Modbus;

public sealed record ModbusPreviewValue(string? Type, string? Text, string Quality, string? Reason, bool UnsafeJavaScriptInteger);
public sealed record ModbusPreviewOrder(string ByteOrder, ModbusPreviewValue Value);
public sealed record ModbusPreviewPoint(string Key, int Block, byte FunctionCode, int Offset, int RegisterNumber,
    string ManualReference, string[] RawHex, string? Binary, ModbusPreviewValue Value, ModbusPreviewOrder[] Orders);
public sealed record ModbusPreviewBlock(byte FunctionCode, int Offset, int Count, string Outcome, bool? Requested,
    byte? ExceptionCode, DateTimeOffset? ReadAt);
public sealed record ModbusReadPreviewResult(byte UnitId, int TotalPoints, bool Truncated, bool DeadlineExceeded,
    double ElapsedMs, ModbusPreviewBlock[] Blocks, ModbusPreviewPoint[] Points);

/// <summary>单次、只读、不入队、不存盘。每块借用正式串行会话；显示结果按实际 UTF-8 JSON 字节有界。
/// 超时保留明确完成的块，未完成的块无旧值；64 位值只返回原文附类型。</summary>
public sealed class ModbusReadPreview(ISharedConnections connections, IOutboundConnector connector, TimeProvider time)
{
    public const int MaximumDisplayBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly (string Name, ModbusByteOrder Order)[] Orders =
        [("ABCD", ModbusByteOrder.Abcd), ("CDAB", ModbusByteOrder.Cdab), ("BADC", ModbusByteOrder.Badc), ("DCBA", ModbusByteOrder.Dcba)];

    public async Task<ModbusReadPreviewResult> RunAsync(ModbusConnectionSettings settings, byte unit,
        ImmutableArray<ModbusPointSettings> points, CancellationToken cancellationToken,
        int timeoutMs = 30000, int maxRegisters = 125, int maxBits = 2000)
    {
        settings = settings.Validate();
        if (settings.Format == ModbusFrameFormat.RtuOverTcp && unit is < 1 or > 247 || timeoutMs is < 100 or > 60000)
        { throw new ArgumentException("预览 UnitId 或总期限无效。"); }
        var table = new ModbusPointTable(points, maxRegisters, maxBits);
        var enabled = points.Where(point => point.Enabled).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        IConnectionLease<ModbusConnectionSession> lease;
        try
        { lease = connections.Acquire(settings.ConnectionId, "modbus", settings.Identity, () => new ModbusConnectionSession(settings, null, connector, time)); }
        catch (InvalidOperationException) { throw new ModbusConnectionUnavailableException(); }
        await using var ownership = lease;
        var rows = new List<ModbusPreviewPoint>();
        var blocks = new List<ModbusPreviewBlock>();
        // 包装字段、数组分隔符及浮点/时间元数据留足空间；逐行/块计量，包括 JSON 转义。
        var bytes = 1024;
        var truncated = false;
        var expired = false;
        string? skip = null;
        foreach (var block in table.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes >= MaximumDisplayBytes - 2048)
            { truncated = true; break; }
            ModbusReadResult? result = null;
            DateTimeOffset? readAt = null;
            var outcome = skip;
            if (skip is null)
            {
                try
                {
                    result = await lease.ExecuteAsync((session, token) =>
                        session.ReadPreviewAsync(unit, block.FunctionCode, block.Start, block.Count, token), operation.Token);
                    if (result.WasRequested)
                    { readAt = time.GetUtcNow(); }
                    outcome = result.Outcome switch
                    {
                        ModbusRequestOutcome.Success => "success",
                        ModbusRequestOutcome.Timeout => "readTimeout",
                        ModbusRequestOutcome.DeviceException => "deviceException",
                        ModbusRequestOutcome.ConnectionError => "connectionError",
                        _ => "readError"
                    };
                    if (result.Outcome is ModbusRequestOutcome.Timeout or ModbusRequestOutcome.ConnectionError)
                    { skip = outcome; }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                { expired = true; outcome = skip = "deadline"; }
            }
            var report = new ModbusPreviewBlock(block.FunctionCode, block.Start, block.Count, outcome!,
                result?.WasRequested ?? (outcome == "deadline" ? null : false), result?.ExceptionCode, readAt);
            var blockBytes = JsonSerializer.SerializeToUtf8Bytes(report, Json).Length + 1;
            if (bytes + blockBytes > MaximumDisplayBytes)
            { truncated = true; break; }
            var blockIndex = blocks.Count;
            blocks.Add(report);
            bytes += blockBytes;
            foreach (var member in block.Points)
            {
                var point = enabled[member.PointIndex];
                var row = Render(point, member, blockIndex, result, outcome!);
                var rowBytes = JsonSerializer.SerializeToUtf8Bytes(row, Json).Length + 1;
                if (bytes + rowBytes > MaximumDisplayBytes)
                { truncated = true; break; }
                rows.Add(row);
                bytes += rowBytes;
            }
            if (truncated)
            { break; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(unit, enabled.Length, truncated, expired, time.GetElapsedTime(started).TotalMilliseconds, blocks.ToArray(), rows.ToArray());
    }

    private static ModbusPreviewPoint Render(ModbusPointSettings point, ModbusBlockPoint member, int block,
        ModbusReadResult? result, string outcome)
    {
        var offset = point.Address!.Value;
        var prefix = point.FunctionCode switch { 1 => "0", 2 => "1", 3 => "4", _ => "3" };
        var manual = prefix + (offset + 1).ToString("D5", CultureInfo.InvariantCulture);
        var value = new ModbusPreviewValue(null, null, "bad", outcome, false);
        string[] hex = [];
        string? binary = null;
        ModbusPreviewOrder[] orders = [];
        if (result?.Outcome == ModbusRequestOutcome.Success)
        {
            if (point.FunctionCode <= 2)
            { value = Describe(ModbusRegisterDecoder.DecodeCoil(result.Bits![member.Offset])); }
            else
            {
                var raw = result.Registers!.AsSpan(member.Offset, member.Width);
                hex = raw.ToArray().Select(word => word.ToString("X4", CultureInfo.InvariantCulture)).ToArray();
                value = Describe(ModbusRegisterDecoder.Decode(raw, point.RegisterType, point.Order, point.Bit));
                if (point.Type == "bit")
                { binary = Convert.ToString(raw[0], 2).PadLeft(16, '0'); }
                if (member.Width > 1)
                {
                    orders = new ModbusPreviewOrder[Orders.Length];
                    for (var index = 0; index < Orders.Length; index++)
                    { orders[index] = new(Orders[index].Name, Describe(ModbusRegisterDecoder.Decode(raw, point.RegisterType, Orders[index].Order))); }
                }
            }
        }
        return new(point.Key, block, point.FunctionCode, offset, offset + 1, manual, hex, binary, value, orders);
    }

    private static ModbusPreviewValue Describe(ModbusDecodedValue value) => value.Error is not null
        ? new(null, null, "bad", "nonFinite", false)
        : new(value.Value!.Type switch { FieldType.Boolean => "boolean", FieldType.Integer => "integer", _ => "number" },
            value.Value.ToString(), "good", null,
            value.Value.Type == FieldType.Integer && value.Value.TryGetExactNumber(out var number) && (number > 9007199254740991m || number < -9007199254740991m));
}

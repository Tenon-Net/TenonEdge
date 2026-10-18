using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Modbus;

/// <summary>一次 Prepare 一套绑定；进程级服务只负责装配，不保存请求或跨计划可变状态。</summary>
public sealed class ModbusPlanAdapterProvider(ISharedConnections connections, IOutboundConnector connector, TimeProvider time) : IRuntimePlanAdapterProvider
{
    public RuntimePlanAdapters Create() => new([new ModbusPlanInputFactory(connections, connector, time)], []);
}

internal sealed class ModbusPlanInputFactory(ISharedConnections shared, IOutboundConnector connector, TimeProvider time) : IInputUnitFactory
{
    private readonly ISharedConnections _shared = shared;
    private readonly IOutboundConnector _connector = connector;
    private readonly TimeProvider _time = time;
    private readonly Dictionary<long, Connection> _connections = [];
    private readonly Dictionary<(long Connection, byte Unit), Device> _physicalUnits = [];
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<RuntimeUnitId, Binding> _inputs = [];
    private volatile bool _started;
    public string Kind => "input.modbus";

    public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context)
    {
        try
        {
            if (_inputs.TryGetValue(plan.Id, out var existing))
            {
                if (!ReferenceEquals(existing.Context, context) || existing.Hash != plan.ConfigurationHash)
                { throw new ArgumentException("Modbus 计划工厂不能跨准备计划复用。"); }
                return new Unit(this, existing);
            }
            if (_started || !plan.Id.Kind.StartsWith("input.", StringComparison.Ordinal))
            { throw new ArgumentException("Modbus 计划绑定已冻结或单元种类无效。"); }
            var settings = ModbusInputSettings.Parse(plan.Configuration);
            var table = new ModbusPointTable(settings.Points, settings.MaxRegisters, settings.MaxBits);
            var connection = _connections.GetValueOrDefault(settings.ConnectionId);
            if (connection is not null && connection.Settings.Identity != settings.Connection.Identity)
            { throw new ArgumentException("同一 Modbus 连接的物理参数不一致。"); }
            if (connection is null && _connections.Count >= 256)
            { throw new ArgumentException("Modbus 连接超过 256 个。"); }
            var device = _devices.GetValueOrDefault(context.DeviceKey);
            if (device is not null && (device.ConnectionId != settings.ConnectionId || device.Unit != settings.UnitId))
            { throw new ArgumentException("同一 Modbus 设备必须使用相同连接与 UnitId。"); }
            device ??= _physicalUnits.GetValueOrDefault((settings.ConnectionId, settings.UnitId!.Value));
            if (device is not null && settings.Points.Any(point => device.Keys.Contains((context.DeviceKey, point.Key))))
            { throw new ArgumentException("同一设备的 Modbus 点位 key 不能跨组重复。"); }
            // 完成全部校验之后再登记，失败的构建不能污染已准备的设备/连接。
            connection ??= new Connection(settings.Connection, _time);
            device ??= new Device(settings.ConnectionId, settings.UnitId!.Value, _time);
            var binding = new Binding(plan.Id, plan.ConfigurationHash, settings, table, context, connection, device);
            _connections[settings.ConnectionId] = connection;
            _devices[context.DeviceKey] = device;
            _physicalUnits[(settings.ConnectionId, settings.UnitId!.Value)] = device;
            connection.Units.Add(settings.UnitId!.Value);
            device.PeriodMs = Math.Min(device.PeriodMs, settings.PeriodMs);
            device.Latest.Add(plan.Id, null);
            foreach (var point in settings.Points)
            { device.Keys.Add((context.DeviceKey, point.Key)); }
            _inputs.Add(plan.Id, binding);
            return new Unit(this, binding);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { throw new RuntimeUnitBuildException(plan.Id, "Modbus 输入配置无效，请核对连接、设备、周期与点表。", exception); }
    }

    private sealed class Connection(ModbusConnectionSettings settings, TimeProvider time)
    {
        public ModbusConnectionSettings Settings { get; } = settings;
        public HashSet<byte> Units { get; } = [];
        public Lazy<long> StartedAt { get; } = new(time.GetTimestamp);
    }

    private sealed class Device(long connectionId, byte unit, TimeProvider time)
    {
        public long ConnectionId { get; } = connectionId;
        public byte Unit { get; } = unit;
        public int PeriodMs { get; set; } = int.MaxValue;
        public HashSet<(string Device, string Point)> Keys { get; } = [];
        public Dictionary<RuntimeUnitId, bool?> Latest { get; } = [];
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public ModbusDevicePollingState? State { get; set; }
        public long? FailureAt { get; set; }
        public ModbusRequestOutcome WindowFailure { get; set; }
        public TimeSpan FailureWindow { get; set; }
        public long LastFailureWindow { get; set; } = -1;
        public bool FastFailed => FailureAt is { } at && time.GetElapsedTime(at) < FailureWindow;

        public void Record(Binding input, ModbusCycleResult cycle)
        {
            if (cycle.Overrun || !cycle.HasActualAttempt)
            { return; }
            Latest[input.Id] = cycle.HasSuccessfulRead;
            if (cycle.HasSuccessfulRead)
            { State!.Record(cycle); return; }
            // 多组共享设备，不把同一设备同一最短周期中的多个失败组计为多次离线证据。
            // 未请求的组不是失败；最新成功组也不能被其他组的失败覆盖。
            var window = (long)(time.GetElapsedTime(input.Connection.StartedAt.Value).TotalMilliseconds / PeriodMs);
            if (Latest.Values.All(value => value == false) && window != LastFailureWindow)
            { State!.Record(cycle); LastFailureWindow = window; }
        }
    }

    private sealed record Binding(RuntimeUnitId Id, string Hash, ModbusInputSettings Settings, ModbusPointTable Table,
        InputUnitContext Context, Connection Connection, Device Device);

    private sealed class Unit(ModbusPlanInputFactory owner, Binding input) : IRuntimeUnit
    {
        public RuntimeUnitId Id => input.Id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            try
            {
                await input.Context.WaitForActivationAsync(stoppingToken);
                owner._started = true;
                await using var lease = owner._shared.Acquire(input.Settings.ConnectionId, "modbus", input.Connection.Settings.Identity,
                    () => new ModbusConnectionSession(input.Connection.Settings, input.Connection.Units, owner._connector, owner._time));
                await lease.ExecuteAsync((session, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    session.ConfigureUnits(input.Connection.Units);
                    return Task.FromResult(true);
                }, stoppingToken);
                var period = TimeSpan.FromMilliseconds(input.Settings.PeriodMs);
                var started = input.Connection.StartedAt.Value;
                var cycle = (long)(owner._time.GetElapsedTime(started) / period);
                while (true)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var remaining = period * (cycle + 1) - owner._time.GetElapsedTime(started);
                    if (remaining > TimeSpan.Zero)
                    {
                        using var deadline = new CancellationTokenSource(remaining, owner._time);
                        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                        try
                        {
                            // 最坏每点一块：块结果/数组/字典/FieldValue 与消息构造暂存；按点数先预约，禁止先采集再争预算。
                            var reservation = 8192L + input.Settings.Points.Length * 1024L;
                            await input.Context.Messages.ProduceAsync(reservation, reservation, async token =>
                            {
                                await input.Device.Serial.WaitAsync(token);
                                try
                                {
                                    input.Device.State ??= new ModbusDevicePollingState(TimeSpan.FromMilliseconds(input.Device.PeriodMs), owner._time);
                                    if (input.Device.FastFailed)
                                    {
                                        input.Context.Reads.ReadFailed(owner._time.GetUtcNow(), input.Device.WindowFailure == ModbusRequestOutcome.Timeout
                                            ? "Modbus 同设备本周期前序读取超时，跳过剩余采集块。"
                                            : "Modbus 同设备本周期连接失败，跳过剩余采集块。");
                                        return null;
                                    }
                                    // 先报告本窗口快速失败，再检查后续周期退避；否则第三次失败的后续组会漏掉离线诊断。
                                    if (!input.Device.State.CanRead)
                                    { return null; }
                                    var left = period * (cycle + 1) - owner._time.GetElapsedTime(started);
                                    var result = await ModbusCycleReader.ReadAsync(lease, input.Settings.UnitId!.Value, input.Table.Blocks, left, owner._time, token);
                                    if (result.HasActualAttempt && result.Blocks.FirstOrDefault(block => block.Outcome is ModbusRequestOutcome.Timeout or ModbusRequestOutcome.ConnectionError) is { } failure)
                                    {
                                        input.Device.FailureAt = owner._time.GetTimestamp();
                                        input.Device.WindowFailure = failure.Outcome;
                                        var devicePeriod = TimeSpan.FromMilliseconds(input.Device.PeriodMs);
                                        var deviceWindow = (long)(owner._time.GetElapsedTime(started) / devicePeriod);
                                        input.Device.FailureWindow = devicePeriod * (deviceWindow + 1) - owner._time.GetElapsedTime(started);
                                        foreach (var group in input.Device.Latest.Keys)
                                        { input.Device.Latest[group] = false; }
                                    }
                                    input.Device.Record(input, result);
                                    if (result.Overrun)
                                    { input.Context.Reads.CycleSkipped(owner._time.GetUtcNow()); return null; }
                                    var message = input.Table.Decode(result, input.Context.InputKey, input.Context.DeviceKey, owner._time.GetUtcNow(), input.Context.GatewayTimeSource);
                                    if (message is not null)
                                    { input.Context.Reads.ReadCompleted(message.ReceivedAt, message.Quality.Count); }
                                    else if (result.HasActualAttempt)
                                    {
                                        var code = result.Blocks.FirstOrDefault(block => block.ExceptionCode is not null)?.ExceptionCode;
                                        input.Context.Reads.ReadFailed(owner._time.GetUtcNow(), code is { } value ? $"Modbus 设备异常码 {value}。" : "Modbus 本周期读取失败。");
                                    }
                                    return message;
                                }
                                finally { input.Device.Serial.Release(); }
                            }, operation.Token);
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        { input.Context.Reads.CycleSkipped(owner._time.GetUtcNow()); }
                        catch (InputMessageSizeException)
                        { input.Context.Reads.ReadFailed(owner._time.GetUtcNow(), "Modbus 点表超出输入预算。", InputReadFailureKind.DataError); }
                    }
                    else
                    { input.Context.Reads.CycleSkipped(owner._time.GetUtcNow()); }
                    cycle++;
                    var due = (long)(owner._time.GetElapsedTime(started) / period);
                    for (; cycle < due; cycle++)
                    { input.Context.Reads.CycleSkipped(owner._time.GetUtcNow()); }
                    var delay = period * cycle - owner._time.GetElapsedTime(started);
                    if (delay > TimeSpan.Zero)
                    { await Task.Delay(delay, owner._time, stoppingToken); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

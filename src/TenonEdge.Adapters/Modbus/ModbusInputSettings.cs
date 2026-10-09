using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Modbus;

/// <summary>运行计划的规范化协议偏移；手册编号转换在草稿编辑/导入边界完成，不在采集时猜测。</summary>
public sealed record ModbusInputSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public long ConnectionId { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 502;
    public string FrameFormat { get; init; } = "tcp";
    public int ConnectTimeoutMs { get; init; } = 5000;
    public int ReadTimeoutMs { get; init; } = 5000;
    public int RequestIntervalMs { get; init; }
    public int RtuSilenceMs { get; init; } = 500;
    public byte? UnitId { get; init; }
    public int PeriodMs { get; init; } = 5000;
    public int MaxRegisters { get; init; } = 125;
    public int MaxBits { get; init; } = 2000;
    public ImmutableArray<ModbusPointSettings> Points { get; init; }

    internal ModbusConnectionSettings Connection => new()
    {
        ConnectionId = ConnectionId,
        Host = Host,
        Port = Port,
        ConnectTimeoutMs = ConnectTimeoutMs,
        ReadTimeoutMs = ReadTimeoutMs,
        RequestIntervalMs = RequestIntervalMs,
        RtuSilenceMs = RtuSilenceMs,
        Format = FrameFormat switch
        {
            "tcp" => ModbusFrameFormat.Tcp,
            "rtu-over-tcp" => ModbusFrameFormat.RtuOverTcp,
            _ => throw new ArgumentException("Modbus 帧格式无效。")
        }
    };

    public string ConnectionIdentity() => Connection.Identity;

    public static ModbusInputSettings Parse(JsonElement configuration)
    {
        var settings = configuration.Deserialize<ModbusInputSettings>(Options) ?? throw new ArgumentException("Modbus 输入配置不能为空。");
        var connection = settings.Connection.Validate();
        if (settings.UnitId is null || (connection.Format == ModbusFrameFormat.RtuOverTcp && settings.UnitId is < 1 or > 247))
        { throw new ArgumentException("Modbus UnitId 无效或未设置。"); }
        if (settings.PeriodMs is < 1000 or > 86400000)
        { throw new ArgumentException("Modbus 周期须为 1 秒至 1 天。"); }
        _ = new ModbusPointTable(settings.Points, settings.MaxRegisters, settings.MaxBits);
        return settings with { Host = connection.Host };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModbusPointSettings
{
    public string Key { get; init; } = string.Empty;
    public byte FunctionCode { get; init; }
    public int? Address { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? ByteOrder { get; init; }
    public int? Bit { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Name { get; init; }
    public string? Unit { get; init; }

    internal ModbusRegisterType RegisterType => Type switch
    {
        "bit" => ModbusRegisterType.Bit,
        "int16" => ModbusRegisterType.Int16,
        "uint16" => ModbusRegisterType.UInt16,
        "int32" => ModbusRegisterType.Int32,
        "uint32" => ModbusRegisterType.UInt32,
        "float32" => ModbusRegisterType.Float32,
        "int64" => ModbusRegisterType.Int64,
        "uint64" => ModbusRegisterType.UInt64,
        "float64" => ModbusRegisterType.Float64,
        _ => throw new ArgumentException("Modbus 寄存器类型无效。")
    };
    internal ModbusByteOrder? Order => ByteOrder switch
    {
        null => null,
        "ABCD" => ModbusByteOrder.Abcd,
        "CDAB" => ModbusByteOrder.Cdab,
        "BADC" => ModbusByteOrder.Badc,
        "DCBA" => ModbusByteOrder.Dcba,
        _ => throw new ArgumentException("Modbus 字节序无效。")
    };

    /// <summary>草稿与运行共用单点校验；空组/全停用是否可运行由调用方决定。</summary>
    public void ValidateDefinition() => _ = Validate();

    internal ModbusReadPoint Validate()
    {
        BusinessKeys.Validate(Key, nameof(Key));
        if (Name?.Length > 64 || Unit?.Length > 64)
        { throw new ArgumentException("Modbus 点位名称或单位过长。"); }
        if (Address is null)
        { throw new ArgumentException("Modbus 点位缺少协议偏移。"); }
        int width;
        if (FunctionCode is 1 or 2)
        {
            if (Type != "bool" || Bit is not null || ByteOrder is not null)
            { throw new ArgumentException("Modbus 线圈/离散输入只接受 bool，不接受位号或字节序。"); }
            width = 1;
        }
        else
        {
            width = ModbusRegisterDecoder.RegisterCount(RegisterType);
            _ = ModbusRegisterDecoder.Decode(new ushort[width], RegisterType, Order, Bit);
        }
        return new(ModbusAddress.FromOffset(FunctionCode, Address.Value, width), width);
    }
}

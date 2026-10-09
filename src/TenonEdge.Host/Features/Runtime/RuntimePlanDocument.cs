using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 运行计划的文本形式（T1 的种子快照与 schema 版本 1 的运行快照）：
/// <code>
/// { "schemaVersion": 1,
///   "units": [ { "kind": "input.simulator", "key": "meter-001/simulated", "configuration": { "periodMs": 5000 } } ] }
/// </code>
/// <list type="bullet">
/// <item>解析时校验单元标识、配置为 JSON 对象、标识不重复；未知的属性视为错误，不悄悄忽略。</item>
/// <item>规范化内容：单元按标识排序，配置使用 <see cref="RuntimeUnitPlan"/> 的规范化形式，因此与种子文件的格式、属性顺序无关；
/// 内容哈希为规范化文本的 SHA-256，作为快照的 content_hash 与种子的 seed_hash。</item>
/// </list>
/// 运行计划只含非秘密配置；凭证不进入快照（总设计 §7.4）。
/// </summary>
internal sealed class RuntimePlanDocument
{
    public const int SchemaVersion = 1;

    /// <summary>种子文件的大小上限（同总设计 §8.4 的配置导入文件上限）。</summary>
    public const long MaximumBytes = 16 * 1024 * 1024;

    private static readonly HashSet<string> TopLevelProperties = new(StringComparer.Ordinal) { "schemaVersion", "units" };

    private static readonly HashSet<string> UnitProperties = new(StringComparer.Ordinal) { "kind", "key", "configuration" };

    private RuntimePlanDocument(IReadOnlyList<RuntimeUnitPlan> units, byte[] canonical)
    {
        Units = units;
        Canonical = canonical;
        ContentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    public IReadOnlyList<RuntimeUnitPlan> Units { get; }

    /// <summary>规范化的 UTF-8 文本。</summary>
    public byte[] Canonical { get; }

    public string CanonicalText => Encoding.UTF8.GetString(Canonical);

    public string ContentHash { get; }

    /// <summary>按版本号生成不可变的运行计划。</summary>
    public RuntimePlan ToPlan(long revision) => new(revision, Units);

    /// <summary>解析运行计划文本；内容无效时抛出 <see cref="RuntimePlanDocumentException"/>，说明面向管理员。</summary>
    public static RuntimePlanDocument Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            return FromElement(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new RuntimePlanDocumentException($"不是有效的 JSON（行 {exception.LineNumber}，字节位置 {exception.BytePositionInLine}）。");
        }
    }

    /// <summary>计算规范化文本的内容哈希，用于加载快照时核对。</summary>
    public static string HashOf(string canonicalText) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));

    private static RuntimePlanDocument FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RuntimePlanDocumentException("顶层应为 JSON 对象。");
        }

        RejectUnknown(root, TopLevelProperties, "顶层");
        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var schemaVersion) || schemaVersion != SchemaVersion)
        {
            throw new RuntimePlanDocumentException($"schemaVersion 应为 {SchemaVersion}。");
        }

        if (!root.TryGetProperty("units", out var unitsElement) || unitsElement.ValueKind != JsonValueKind.Array)
        {
            throw new RuntimePlanDocumentException("units 应为数组。");
        }

        var units = new List<RuntimeUnitPlan>();
        var index = 0;
        foreach (var unit in unitsElement.EnumerateArray())
        {
            units.Add(ParseUnit(unit, index++));
        }

        RuntimePlan plan;
        try
        {
            // 借用运行计划的构造检查重复标识，并得到按标识排序的单元。
            plan = new RuntimePlan(1, units);
        }
        catch (ArgumentException)
        {
            throw new RuntimePlanDocumentException("运行单元标识重复或计划结构无效。");
        }

        var httpConnections = new Dictionary<long, (string Base, int Timeout)>();
        foreach (var unit in plan.Units.Where(unit => unit.Id.Kind is "input.http" or "output.http"))
        {
            HttpRequestSettings settings = unit.Id.Kind == "input.http" ? HttpInputSettings.Parse(unit.Configuration) : HttpOutputSettings.Parse(unit.Configuration);
            var identity = (settings.BaseAddress, settings.TimeoutMs);
            if (httpConnections.TryGetValue(settings.ConnectionId, out var previous) && previous != identity)
            { throw new RuntimePlanDocumentException("同一 HTTP 连接的基础地址与超时必须一致。"); }
            httpConnections[settings.ConnectionId] = identity;
        }
        var mqttConnections = new Dictionary<long, string>();
        var mqttClients = new Dictionary<(string Recipient, string ClientId), long>();
        foreach (var unit in plan.Units.Where(unit => unit.Id.Kind is "input.mqtt" or "output.mqtt"))
        {
            MqttConnectionSettings settings = unit.Id.Kind == "input.mqtt"
                ? MqttInputSettings.Parse(unit.Configuration) : MqttOutputSettings.Parse(unit.Configuration);
            var identity = settings.ConnectionIdentity(settings.ClientId);
            if (httpConnections.ContainsKey(settings.ConnectionId) ||
                mqttConnections.TryGetValue(settings.ConnectionId, out var previous) && previous != identity)
            { throw new RuntimePlanDocumentException("同一连接的协议与 MQTT 连接参数必须一致。"); }
            mqttConnections[settings.ConnectionId] = identity;
            if (settings.ClientId is { } clientId)
            {
                var client = (settings.Recipient, clientId);
                if (mqttClients.TryGetValue(client, out var other) && other != settings.ConnectionId)
                { throw new RuntimePlanDocumentException("同一 Broker 的不同连接不能使用相同 ClientId。"); }
                mqttClients[client] = settings.ConnectionId;
            }
        }
        ValidateModbus(plan, httpConnections.Keys.Concat(mqttConnections.Keys).ToHashSet());
        try
        { MqttFeedbackGuard.Validate(plan.Units); }
        catch (ArgumentException)
        { throw new RuntimePlanDocumentException("MQTT 输入与输出存在已知反馈闭环，请调整发布主题或订阅过滤器。"); }
        return new RuntimePlanDocument(plan.Units, Serialize(plan.Units));
    }

    private static void ValidateModbus(RuntimePlan plan, HashSet<long> otherConnections)
    {
        var connections = new Dictionary<long, string>();
        var devices = new Dictionary<string, (long Connection, byte Unit)>(StringComparer.Ordinal);
        var keys = new HashSet<(string Device, string Point)>();
        foreach (var unit in plan.Units.Where(unit => unit.Id.Kind == "input.modbus"))
        {
            var settings = ModbusInputSettings.Parse(unit.Configuration);
            var identity = settings.ConnectionIdentity();
            if (otherConnections.Contains(settings.ConnectionId) ||
                connections.TryGetValue(settings.ConnectionId, out var previous) && previous != identity)
            { throw new RuntimePlanDocumentException("同一连接的协议与 Modbus 物理参数必须一致。"); }
            connections[settings.ConnectionId] = identity;
            var parts = unit.Id.Key.Split('/');
            if (parts.Length != 2)
            { throw new RuntimePlanDocumentException("Modbus 输入标识必须为 deviceKey/inputKey。"); }
            var device = (settings.ConnectionId, settings.UnitId!.Value);
            if (devices.TryGetValue(parts[0], out var priorDevice) && priorDevice != device)
            { throw new RuntimePlanDocumentException("同一 Modbus 设备必须使用相同连接与 UnitId。"); }
            devices[parts[0]] = device;
            foreach (var point in settings.Points)
            {
                if (!keys.Add((parts[0], point.Key)))
                { throw new RuntimePlanDocumentException("同一 Modbus 设备的点位 key 不可跨组重复。"); }
            }
        }
        if (connections.Count > 256 || connections.Count + otherConnections.Count > 500)
        { throw new RuntimePlanDocumentException("连接数量超过上限：全部 500 个、Modbus 256 个。"); }
    }

    private static RuntimeUnitPlan ParseUnit(JsonElement unit, int index)
    {
        if (unit.ValueKind != JsonValueKind.Object)
        {
            throw new RuntimePlanDocumentException($"units[{index}] 应为 JSON 对象。");
        }

        RejectUnknown(unit, UnitProperties, $"units[{index}]");
        if (!unit.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !unit.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
        {
            throw new RuntimePlanDocumentException($"units[{index}] 缺少字符串 kind 或 key。");
        }

        if (!unit.TryGetProperty("configuration", out var configuration))
        {
            throw new RuntimePlanDocumentException($"units[{index}] 缺少 configuration。");
        }

        try
        {
            if (kind.GetString() == "output.mqtt")
            {
                // 冻结前拒绝未知属性（尤其密码等旁路），不把被拒的秘密配置写进未生效快照。
                MqttOutputSettings.Parse(configuration);
            }

            if (kind.GetString() == "output.http")
            {
                HttpOutputSettings.Parse(configuration);
            }

            if (kind.GetString() is "output.http" or "output.mqtt" && configuration.TryGetProperty("mapping", out var mapping))
            { FieldMappingConfiguration.Parse(mapping); }
            if (kind.GetString() == "input.mqtt")
            {
                var settings = MqttInputSettings.Parse(configuration);
                new JsonInputDecoderFactory().Build(settings.Fields, settings.UpstreamTime);
            }
            if (kind.GetString() == "input.modbus")
            { ModbusInputSettings.Parse(configuration); }
            if (kind.GetString() == "input.http")
            {
                var settings = HttpInputSettings.Parse(configuration);
                new JsonInputDecoderFactory().Build(settings.Fields, settings.UpstreamTime);
            }
            return new RuntimeUnitPlan(RuntimeUnitId.Create(kind.GetString()!, key.GetString()!), configuration);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            // 配置解析器的消息、JSON Path 和异常链可能含被拒绝的秘密，不能作为诊断透传。
            throw new RuntimePlanDocumentException($"units[{index}] 的标识或配置无效，请检查该单元的字段、类型和取值。");
        }
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> known, string where)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                throw new RuntimePlanDocumentException($"{where} 有未知的属性，请检查字段名称。");
            }
        }
    }

    private static byte[] Serialize(IEnumerable<RuntimeUnitPlan> units)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("units");
            foreach (var unit in units)
            {
                // 属性按名称的序号排序，与 RuntimeUnitPlan 的规范化规则一致。
                writer.WriteStartObject();
                writer.WritePropertyName("configuration");
                unit.Configuration.WriteTo(writer);
                writer.WriteString("key", unit.Id.Key);
                writer.WriteString("kind", unit.Id.Kind);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>运行计划文本无效；说明面向管理员。</summary>
internal sealed class RuntimePlanDocumentException : Exception
{
    public RuntimePlanDocumentException()
    {
    }

    public RuntimePlanDocumentException(string message)
        : base(message)
    {
    }

    public RuntimePlanDocumentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

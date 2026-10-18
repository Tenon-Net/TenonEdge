using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>编译期登记的版本化输出模块；参数不能包含凭证。只注册此接口，由目录接入原输出工厂。
/// 声明/协议绑定为纯本地、并发安全计算，不得联网或创建后台任务。
/// 不扩展发送缓存；CreateMessage、CreateAdapter 和发送均由原分发器/发送监督者调用。</summary>
public interface IOutputModuleFactory
{
    string Kind { get; }
    int Version { get; }
    string DisplayName => Kind;
    Parameters.ParameterSchema Parameters { get; }

    /// <summary>声明已有 HTTP/MQTT 协议的完整非秘密输出配置（含 inputs/connectionId），
    /// 不包含映射/转换阶段；阶段由原分发器执行。只做本地计算；运行时使用同次 Prepare 的原工厂。
    /// 新连接/传输类型的注册另需宿主支持，不能以未描述的自建客户端旁路目标保护。</summary>
    OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context);
}

public sealed record OutputProtocolBinding(string Kind, JsonElement Configuration);

/// <summary>不可变、无请求/数据库/客户端依赖的输出构建上下文。参数引用按名称绑定；
/// 一个输出只向一个显式 Connection 生成投递，多个路径共用原目标发送者。</summary>
public sealed class OutputModuleContext
{
    public OutputModuleContext(long connectionId, IEnumerable<string> inputs, IReadOnlyDictionary<string, ModuleConnectionSnapshot> connections)
    {
        ConnectionId = connectionId;
        Inputs = inputs.ToImmutableArray();
        Connections = connections.ToImmutableDictionary(StringComparer.Ordinal);
        if (!Connections.Values.Any(connection => connection.ConnectionId == connectionId) ||
            Inputs.Length is < 1 or > 2000 || Inputs.Distinct(StringComparer.Ordinal).Count() != Inputs.Length ||
            Inputs.Any(input => RuntimeUnitId.Create("input.simulator", input).KeySegments.Count != 2))
        { throw new ArgumentException("输出来源或目标连接无效。"); }
    }
    public long ConnectionId { get; }
    public ImmutableArray<string> Inputs { get; }
    public ImmutableDictionary<string, ModuleConnectionSnapshot> Connections { get; }
}

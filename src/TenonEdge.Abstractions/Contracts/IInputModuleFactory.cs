using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>编译期登记的输入模块。沿用原输入工厂/实例生命周期，不另建采集器或后台服务。
/// 只注册此接口一次，由模块目录向原运行时提供同一个工厂的受校验视图。
/// 参数与字段描述不可包含秘密；Describe 只做本地校验，不构建实例、不访问网络/数据库。
/// Build/RecoveryGroup 接收去掉模块封套后的原参数，标识和 InputUnitContext 不变。
/// 连接引用通过 InputUnitContext.Connections 提供冻结的非秘密连接，不以地址或秘密标量旁路。
/// 支持的连接类型由宿主准入控制；联网须复用原共享连接、出站与凭证解析机制。</summary>
public interface IInputModuleFactory : IInputUnitFactory
{
    int Version { get; }
    string DisplayName => Kind;
    ParameterSchema Parameters { get; }
    ImmutableArray<InputModuleField> Describe(JsonElement parameters);

    /// <summary>可选的纯本地协议委托。返回已有输入协议的完整非秘密配置，编译时冻结；
    /// 运行时必须由同一次 Prepare 的原协议工厂构建，不另建 MQTT/Modbus 计划工厂。
    /// 返回 null 时仍调用本模块的 Build。不得访问网络、数据库或保存可变状态。</summary>
    InputProtocolBinding? BindProtocol(JsonElement parameters, IReadOnlyDictionary<string, ModuleConnectionSnapshot> connections) => null;
}

public sealed record InputModuleField(string Key, FieldType Type, string? Unit);

/// <summary>复用已有 input.http/input.mqtt/input.modbus 的采集、共享与确认逻辑；不是新的运行单元。
/// 配置必须引用声明的同协议连接，协议参数由宿主校验，不包含凭证秘密。</summary>
public sealed record InputProtocolBinding(string Kind, JsonElement Configuration);

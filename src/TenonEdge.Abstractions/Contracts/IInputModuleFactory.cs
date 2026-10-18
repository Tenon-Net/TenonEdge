using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>编译期登记的输入模块。沿用原输入工厂/实例生命周期，不另建采集器或后台服务。
/// 只注册此接口一次，由模块目录向原运行时提供同一个工厂的受校验视图。
/// 参数与字段描述不可包含秘密；Describe 只做本地校验，不构建实例、不访问网络/数据库。
/// Build/RecoveryGroup 接收去掉模块封套后的原参数，标识和 InputUnitContext 不变。
/// 当前接通自包含的标量输入；连接引用须另经连接快照编译，不能放任意地址或秘密旁路。</summary>
public interface IInputModuleFactory : IInputUnitFactory
{
    int Version { get; }
    string DisplayName => Kind;
    ParameterSchema Parameters { get; }
    ImmutableArray<InputModuleField> Describe(JsonElement parameters);
}

public sealed record InputModuleField(string Key, FieldType Type, string? Unit);

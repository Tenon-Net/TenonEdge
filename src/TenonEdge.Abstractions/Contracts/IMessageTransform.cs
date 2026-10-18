using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>编译期注册的可信纯转换。Build 只校验和编译非秘密参数，不做 I/O、不持有作用域服务。
/// 同一 kind/version 的配置语义须稳定；不扫描 DLL，不执行脚本。复杂配置须由 Build 严格校验未知属性。</summary>
public interface IMessageTransformFactory
{
    string Kind { get; }
    int Version { get; }
    string DisplayName => Kind;
    /// <summary>标量自动表单声明；复杂配置（如映射规则）可无标量声明，由专用编辑器与 Build 校验。</summary>
    ParameterSchema? Parameters { get; }
    IMessageTransform Build(JsonElement configuration);
}

/// <summary>不可变、可并发使用的本条消息转换，不访问网络/数据库，不缓存跨消息状态。
/// 结果必须保留来源身份、时间、原始 JSON 和诊断元数据；只能改变值和质量。
/// 预算包含临时分配与结果，不包含借用且不变的源消息；并非不可信代码的内存/CPU 沙箱。</summary>
public interface IMessageTransform
{
    ImmutableArray<TransformField> Schema { get; }
    ImmutableArray<string> ReferencedFields { get; }
    long MaximumResultBytes { get; }
    long WorkingReservationBytes { get; }
    MessageTransformResult Apply(EdgeMessage source);
}

public sealed record TransformField(string Key, FieldType Type, string? Unit);
/// <summary>Message 为 null 表示必填失败，整条路径拒绝；计数只含本阶段，不能返回半成品。</summary>
public sealed record MessageTransformResult(EdgeMessage? Message, int InvalidFields = 0, int MissingFields = 0)
{
    public ImmutableArray<TransformIssue> Issues { get; init; } = [];
}
public sealed record TransformIssue(string Field, QualityCode? Reason, bool Required);

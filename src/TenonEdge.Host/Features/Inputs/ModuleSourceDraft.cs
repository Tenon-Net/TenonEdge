using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.Host.Features.Inputs;

/// <summary>非秘密来源参数。字段由已安装模块在事务外描述，不把客户端声称的字段写成可信元数据。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModuleSourceDraft(string Kind, int Version, JsonElement Parameters)
{
    internal RuntimeUnitPlan ToUnit(string deviceKey, string inputKey) => new(RuntimeUnitId.Create(Kind, deviceKey + "/" + inputKey),
        JsonSerializer.SerializeToElement(new { inputModule = new { version = Version, parameters = Parameters } }));

    // History/export and transaction-local consistency checks do not execute module code.
    // New admission must separately Describe through the installed catalog outside a DB transaction.
    internal ModuleSourceDraft Normalize(DeviceDraft device, string inputKey)
    {
        if (device.Kind != DeviceKind.Module || device.ModuleKind != Kind || Version < 1 || Parameters.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("输入模块与设备类型不一致。"); }
        InputModuleCatalog.ValidateStoredShape(ToUnit(device.Key, inputKey));
        return this with { Parameters = Parameters.Clone() };
    }
}

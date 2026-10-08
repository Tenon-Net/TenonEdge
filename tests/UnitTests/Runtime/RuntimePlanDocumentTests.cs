using System.Text;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>
/// 种子快照与运行快照的文本形式（T1-01）：规范化内容与内容哈希不随格式和顺序变化；结构错误、未知属性与重复标识被拒绝。
/// </summary>
public sealed class RuntimePlanDocumentTests
{
    private const string Seed = """
        {
          "units": [
            { "key": "meter-002/simulated", "kind": "input.simulator", "configuration": { "seed": 2, "periodMs": 1000 } },
            { "kind": "input.simulator", "key": "meter-001/simulated", "configuration": { "periodMs": 1000, "seed": 1 } }
          ],
          "schemaVersion": 1
        }
        """;

    [Fact]
    public void CanonicalContentAndHashDoNotDependOnFormattingOrOrder()
    {
        var document = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(Seed));
        var compact = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"units":[{"configuration":{"periodMs":1000,"seed":1},"key":"meter-001/simulated","kind":"input.simulator"},{"configuration":{"periodMs":1000,"seed":2},"key":"meter-002/simulated","kind":"input.simulator"}]}"""));

        Assert.Equal(
            """{"schemaVersion":1,"units":[{"configuration":{"periodMs":1000,"seed":1},"key":"meter-001/simulated","kind":"input.simulator"},{"configuration":{"periodMs":1000,"seed":2},"key":"meter-002/simulated","kind":"input.simulator"}]}""",
            document.CanonicalText);
        Assert.Equal(compact.ContentHash, document.ContentHash);
        Assert.Equal(RuntimePlanDocument.HashOf(document.CanonicalText), document.ContentHash);
        Assert.StartsWith("sha256:", document.ContentHash, StringComparison.Ordinal);

        var plan = document.ToPlan(7);
        Assert.Equal(7, plan.Revision);
        Assert.Equal(["meter-001/simulated", "meter-002/simulated"], plan.Units.Select(unit => unit.Id.Key));
    }

    [Fact]
    public void AChangedConfigurationChangesTheHash()
    {
        var document = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(Seed));
        var changed = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(Seed.Replace("\"seed\": 2", "\"seed\": 3", StringComparison.Ordinal)));

        Assert.NotEqual(document.ContentHash, changed.ContentHash);
    }

    [Theory]
    [InlineData("not json", "JSON")]
    [InlineData("[]", "顶层")]
    [InlineData("""{"units":[]}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":2,"units":[]}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":1}""", "units")]
    [InlineData("""{"schemaVersion":1,"units":[],"paths":[]}""", "paths")]
    [InlineData("""{"schemaVersion":1,"units":[1]}""", "units[0]")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"a/b"}]}""", "configuration")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"a/b","configuration":{},"enabled":true}]}""", "enabled")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"Input","key":"a/b","configuration":{}}]}""", "units[0]")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"a/b","configuration":[]}]}""", "units[0]")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"a/b","configuration":{}},{"kind":"input.simulator","key":"a/b","configuration":{"x":1}}]}""", "重复")]
    public void InvalidDocumentsAreRejectedWithAReason(string json, string expected)
    {
        var exception = Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPlanIsValid()
    {
        var document = RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

        Assert.Empty(document.ToPlan(1).Units);
        Assert.IsType<RuntimePlan>(document.ToPlan(1));
    }
}

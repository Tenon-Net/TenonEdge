using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.UnitTests.Abstractions;

/// <summary>
/// 运行计划契约（T0-10；总设计 §7.4、§12 T0）：运行单元有确定性标识与配置哈希；配置哈希与属性顺序、空白和转义写法无关，
/// 内容变化则哈希变化；计划不可变，同一单元标识只能出现一次。
/// </summary>
public sealed class RuntimePlanTests
{
    [Fact]
    public void UnitIdsAreDeterministic()
    {
        var first = RuntimeUnitId.Create("input.modbus", "meter-1");
        var second = RuntimeUnitId.Create("input.modbus", "meter-1");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("input.modbus/meter-1", first.ToString());
        Assert.NotEqual(first, RuntimeUnitId.Create("input.modbus", "meter-2"));
        Assert.NotEqual(first, RuntimeUnitId.Create("input.mqtt", "meter-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Input.modbus")]
    [InlineData("input..modbus")]
    [InlineData("input.")]
    [InlineData(".input")]
    [InlineData("input-modbus")]
    [InlineData("1input")]
    [InlineData("input.modbus\n")]
    public void InvalidKindsAreRejected(string kind) =>
        Assert.Throws<ArgumentException>(() => RuntimeUnitId.Create(kind, "meter-1"));

    [Theory]
    [InlineData("")]
    [InlineData("-meter")]
    [InlineData("meter 1")]
    [InlineData("电表")]
    [InlineData("meter-1\n")]
    [InlineData("meter//temperature")]
    [InlineData("/temperature")]
    [InlineData("meter-1/")]
    [InlineData("meter-1/-temperature")]
    [InlineData("a/b/c/d/e")]
    public void InvalidKeysAreRejected(string key) =>
        Assert.Throws<ArgumentException>(() => RuntimeUnitId.Create("input.modbus", key));

    [Fact]
    public void KeysScopedToADeviceAreAccepted()
    {
        // 数据来源的 inputKey 只在设备内唯一，按 deviceKey/inputKey 带上作用域（总设计 §10.5）。
        var first = RuntimeUnitId.Create("input.mqtt", "meter-1/temperature");

        Assert.Equal("input.mqtt/meter-1/temperature", first.ToString());
        Assert.Equal(first, RuntimeUnitId.Create("input.mqtt", "meter-1/temperature"));
        Assert.NotEqual(first, RuntimeUnitId.Create("input.mqtt", "meter-2/temperature"));
        Assert.NotNull(RuntimeUnitId.Create("input.mqtt", "a/b/c/d"));
    }

    [Fact]
    public void IdentifiersLongerThan64CharactersAreRejected()
    {
        Assert.NotNull(RuntimeUnitId.Create("input.modbus", new string('k', 64)));
        Assert.NotNull(RuntimeUnitId.Create("input.modbus", new string('k', 64) + "/" + new string('k', 64)));
        Assert.Throws<ArgumentException>(() => RuntimeUnitId.Create("input.modbus", new string('k', 65)));
        Assert.Throws<ArgumentException>(() => RuntimeUnitId.Create("input.modbus", "meter-1/" + new string('k', 65)));
        Assert.Throws<ArgumentException>(() => RuntimeUnitId.Create("input." + new string('m', 59), "meter-1"));
    }

    [Fact]
    public void ConfigurationHashIgnoresPropertyOrderWhitespaceAndEscapes()
    {
        var compact = Unit("""{"host":"10.0.0.5","port":502,"points":[{"key":"voltage","address":0}]}""");
        var reformatted = Unit("""
            {
              "points": [ { "address": 0, "key": "voltage" } ],
              "port": 502,
              "host": "10.0.0.5"
            }
            """);

        Assert.Equal(compact.ConfigurationHash, reformatted.ConfigurationHash);
        Assert.Equal(compact.Configuration.GetRawText(), reformatted.Configuration.GetRawText());
        Assert.Equal("""{"host":"10.0.0.5","points":[{"address":0,"key":"voltage"}],"port":502}""", compact.Configuration.GetRawText());
    }

    [Theory]
    [InlineData("""{"port":503}""")] // 数值变化
    [InlineData("""{"port":"502"}""")] // 类型变化
    [InlineData("""{"port":502.0}""")] // 数值保留原文：502 与 502.0 视为不同
    [InlineData("""{"port":502,"extra":null}""")] // 多一个属性
    [InlineData("""{"Port":502}""")] // 属性名区分大小写
    public void ConfigurationHashChangesWithContent(string changed) =>
        Assert.NotEqual(Unit("""{"port":502}""").ConfigurationHash, Unit(changed).ConfigurationHash);

    [Fact]
    public void ArrayOrderIsPartOfTheContent() =>
        Assert.NotEqual(Unit("""{"points":[1,2]}""").ConfigurationHash, Unit("""{"points":[2,1]}""").ConfigurationHash);

    [Fact]
    public void ConfigurationHashIsTheSha256OfTheCanonicalText()
    {
        var unit = Unit("""{ "b": [true, null], "a": "x" }""");

        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("""{"a":"x","b":[true,null]}""")));
        Assert.Equal(expected, unit.ConfigurationHash);
    }

    [Theory]
    [InlineData("""{"port":1,"port":2}""")]
    [InlineData("""{"device":{"key":"a","key":"b"}}""")]
    [InlineData("""{"points":[{"key":"a","key":"a"}]}""")]
    public void DuplicatePropertyNamesAreRejected(string json)
    {
        var exception = Assert.Throws<ArgumentException>(() => Unit(json));
        Assert.Contains("重名属性", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonContentIsAnArgumentError()
    {
        // 孤立的 UTF-16 代理项：解析器接受转义，读取字符串时才失败。
        Assert.Throws<ArgumentException>(() => Unit("""{"name":"\uD800"}"""));

        // 嵌套超过 64 层：调用方用更大的深度上限解析出的配置同样被拒绝。
        var deep = "{\"a\":" + new string('[', 70) + new string(']', 70) + "}";
        using var document = JsonDocument.Parse(deep, new JsonDocumentOptions { MaxDepth = 100 });
        Assert.Throws<ArgumentException>(() => new RuntimeUnitPlan(RuntimeUnitId.Create("input.modbus", "meter-1"), document.RootElement));
    }

    [Fact]
    public void NumbersKeepTheirOriginalText()
    {
        // 数值保留原文：值相同但写法不同的数视为不同的配置。
        var hashes = new HashSet<string>(StringComparer.Ordinal)
        {
            Unit("""{"scale":100}""").ConfigurationHash,
            Unit("""{"scale":1E2}""").ConfigurationHash,
            Unit("""{"scale":1e2}""").ConfigurationHash,
        };

        Assert.Equal(3, hashes.Count);
    }

    [Fact]
    public void ConfigurationMustBeAnObject()
    {
        var id = RuntimeUnitId.Create("input.modbus", "meter-1");
        using var array = JsonDocument.Parse("[1,2]");

        Assert.Throws<ArgumentException>(() => new RuntimeUnitPlan(id, array.RootElement));
        Assert.Throws<ArgumentException>(() => new RuntimeUnitPlan(id, default));
    }

    [Fact]
    public void ConfigurationDoesNotDependOnTheCallersDocument()
    {
        RuntimeUnitPlan unit;
        using (var document = JsonDocument.Parse("""{"port":502}"""))
        {
            unit = new RuntimeUnitPlan(RuntimeUnitId.Create("input.modbus", "meter-1"), document.RootElement);
        }

        // 调用方的文档已经释放，单元中的配置仍可读取。
        Assert.Equal(502, unit.Configuration.GetProperty("port").GetInt32());
    }

    [Fact]
    public void PlanOrdersUnitsByIdentifier()
    {
        var plan = new RuntimePlan(7, [
            Unit("""{}""", "output.mqtt", "broker-1"),
            Unit("""{}""", "input.modbus", "meter-2"),
            Unit("""{}""", "input.modbus", "meter-1"),
        ]);

        Assert.Equal(7, plan.Revision);
        Assert.Equal(
            ["input.modbus/meter-1", "input.modbus/meter-2", "output.mqtt/broker-1"],
            plan.Units.Select(unit => unit.Id.ToString()));
    }

    [Fact]
    public void PlanRejectsTwoUnitsWithTheSameIdentifier()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RuntimePlan(1, [
            Unit("""{"port":502}""", "input.modbus", "meter-1"),
            Unit("""{"port":503}""", "input.modbus", "meter-1"),
        ]));

        Assert.Contains("input.modbus/meter-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimePlan(0, []));
        Assert.Empty(new RuntimePlan(1, []).Units);
    }

    private static RuntimeUnitPlan Unit(string json, string kind = "input.modbus", string key = "meter-1")
    {
        using var document = JsonDocument.Parse(json);
        return new RuntimeUnitPlan(RuntimeUnitId.Create(kind, key), document.RootElement);
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.UnitTests.Parameters;

public sealed class ParameterSchemaTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static ParameterDefinition Number(string? min = null, string? max = null, bool whole = false) =>
        new("value", "数值", ParameterType.Number) { Required = true, MinimumJson = min, MaximumJson = max, WholeNumber = whole };
    private static ParameterSchema Schema(params ParameterDefinition[] fields) => new("example", ParameterScope.Input, fields);

    [Theory]
    [InlineData("9007199254740993", "9007199254740993", "9007199254740993", true)]
    [InlineData("9007199254740992", "9007199254740993", null, false)]
    [InlineData("9007199254740994", null, "9007199254740993", false)]
    [InlineData("0.10000000000000000000000000001", null, "0.1", false)]
    [InlineData("1e-1000", "0", "0", false)]
    [InlineData("-1e-1000", "0", null, false)]
    [InlineData("-0", "0", "0.0", true)]
    [InlineData("1.00e2", "100", "100", true)]
    [InlineData("-1.01", "-2", "-1", true)]
    [InlineData("-1.01", "-1", "0", false)]
    [InlineData("18446744073709551615", "0", "18446744073709551615", true)]
    [InlineData("1e1000001", null, null, false)]
    [InlineData("\"1\"", "0", "2", false)]
    public void NumericBoundsCompareOriginalDigitsWithoutRounding(string value, string? minimum, string? maximum, bool valid)
    {
        var schema = Schema(Number(minimum, maximum));
        var input = Json("{\"value\":" + value + "}");
        Assert.Equal(valid, schema.Validate(input).IsEmpty);
        Assert.Equal(value, input.GetProperty("value").GetRawText());
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1e2", true)]
    [InlineData("-0.0", true)]
    [InlineData("1.1", false)]
    [InlineData("1e-1000", false)]
    public void WholeNumbersAreCheckedExactly(string value, bool valid) =>
        Assert.Equal(valid, Schema(Number(whole: true)).Validate(Json("{\"value\":" + value + "}")).IsEmpty);

    [Fact]
    public void AllSixTypesKeepCredentialMaterialOutsidePublicConfiguration()
    {
        var schema = new ParameterSchema("example.connection", ParameterScope.Connection,
        [
            new("title", "名称", ParameterType.Text) { Required = true, MaximumLength = 8, Description = "纯文本", Group = "基本" },
            Number("1", "10"), new("enabled", "启用", ParameterType.Switch) { Required = true },
            new("mode", "方式", ParameterType.Choice) { Required = true, Options = [new("one", "方式一")] },
            new("password", "密码", ParameterType.Credential) { Required = true },
            new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["mqtt"] },
        ]);
        var values = Json("""{"title":"设备","value":5,"enabled":false,"mode":"one","connection":"9007199254740991"}""");
        var references = new Dictionary<string, string> { ["9007199254740991"] = "mqtt" };
        Assert.Empty(schema.Validate(values, references, new HashSet<string> { "password" }));
        Assert.Contains(schema.Validate(values, references), issue => issue == new ParameterIssue("password", "parameter.required"));
        Assert.Contains(schema.Validate(values, new Dictionary<string, string> { ["9007199254740991"] = "http" }), issue => issue.Code == "parameter.connection");
        var leaked = Json("""{"password":"never-echo-me"}""");
        var issues = schema.Validate(leaked);
        Assert.Contains(issues, issue => issue.Code == "parameter.credentialSeparate");
        Assert.DoesNotContain("never-echo-me", JsonSerializer.Serialize(issues), StringComparison.Ordinal);
        Assert.DoesNotContain("never-echo-me", Assert.Throws<ArgumentException>(() => schema.ThrowIfInvalid(leaked)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text", "null", "parameter.required")]
    [InlineData("text", "\"    \"", "parameter.required")]
    [InlineData("text", "\"abcde\"", "parameter.length")]
    [InlineData("text", "42", "parameter.type")]
    [InlineData("switch", "\"true\"", "parameter.type")]
    [InlineData("choice", "\"missing\"", "parameter.choice")]
    [InlineData("connection", "1", "parameter.connection")]
    [InlineData("connection", "\"01\"", "parameter.connection")]
    [InlineData("connection", "\"9007199254740992\"", "parameter.connection")]
    [InlineData("connection", "\"1\"", "parameter.connection")]
    public void InvalidScalarAndUnresolvedReferencesFailClosed(string name, string value, string code)
    {
        var definitions = new Dictionary<string, ParameterDefinition>
        {
            ["text"] = new("text", "文字", ParameterType.Text) { Required = true, MaximumLength = 4 },
            ["switch"] = new("switch", "开关", ParameterType.Switch),
            ["choice"] = new("choice", "选项", ParameterType.Choice) { Options = [new("ok", "正常")] },
            ["connection"] = new("connection", "引用", ParameterType.ConnectionReference) { ConnectionKinds = ["mqtt"] },
        };
        var result = Schema(definitions[name]).Validate(Json("{\"" + name + "\":" + value + "}"));
        Assert.Equal(new ParameterIssue(name, code), Assert.Single(result));
    }

    [Fact]
    public void DefaultsAreValidatedButNeverSilentlyInsertedAndUnknownNamesAreNotEchoed()
    {
        var field = Number("1", "10") with { DefaultJson = "5" };
        var fields = new[] { field };
        var schema = Schema(fields);
        fields[0] = field with { MinimumJson = "6" };
        var empty = Json("{}");
        Assert.Equal("parameter.required", Assert.Single(schema.Validate(empty)).Code);
        Assert.Equal("{}", empty.GetRawText());
        Assert.Empty(schema.Validate(Json("""{"value":5}""")));
        Assert.Equal(new ParameterIssue("", "parameter.unknown"), Assert.Single(schema.Validate(Json("""{"value":5,"secret-in-a-name":true}"""))));
        Assert.Equal("parameter.duplicate", Assert.Single(schema.Validate(Json("""{"value":5,"value":6}"""))).Code);
        Assert.Equal("parameters.object", Assert.Single(schema.Validate(Json("[]"))).Code);
    }

    public static IEnumerable<object[]> InvalidDeclarations()
    {
        yield return [Number("2", "1")];
        yield return [Number("1.1", "10", true)];
        yield return [Number("0", "10") with { DefaultJson = "11" }];
        yield return [Number() with { MinimumJson = "NaN" }];
        yield return [Number() with { DefaultJson = "\"secret\"" }];
        yield return [new ParameterDefinition("value", "文字", ParameterType.Text)];
        yield return [new ParameterDefinition("value", "文字", ParameterType.Text) { MaximumLength = 65537 }];
        yield return [new ParameterDefinition("value", "开关", ParameterType.Switch) { MinimumJson = "0" }];
        yield return [new ParameterDefinition("value", "选项", ParameterType.Choice)];
        yield return [new ParameterDefinition("value", "选项", ParameterType.Choice) { Options = [new("x", "一"), new("x", "二")] }];
        yield return [new ParameterDefinition("value", "选项", ParameterType.Choice) { Options = [new("x", "一")], DefaultJson = "\"y\"" }];
        yield return [new ParameterDefinition("value", "凭证", ParameterType.Credential)];
        yield return [new ParameterDefinition("value", "引用", ParameterType.ConnectionReference)];
        yield return [new ParameterDefinition("value", "引用", ParameterType.ConnectionReference) { ConnectionKinds = ["mqtt"], DefaultJson = "\"1\"" }];
        yield return [Number() with { Name = "../escape" }];
        yield return [Number() with { DisplayName = "\n" }];
        yield return [Number() with { Options = default }];
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void MalformedOrUnsafeDeclarationsAreRejected(ParameterDefinition definition) =>
        Assert.Throws<ArgumentException>(() => Schema(definition));

    [Fact]
    public void CredentialDefaultIsNeverPublicEvenOnConnectionSchema() =>
        Assert.Throws<ArgumentException>(() => new ParameterSchema("connection.test", ParameterScope.Connection,
            [new("password", "密码", ParameterType.Credential) { DefaultJson = "\"secret\"" }]));

    [Fact]
    public void SizeAndCountBoundsAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => Schema([]));
        Assert.Throws<ArgumentException>(() => Schema(Enumerable.Range(0, 129).Select(i => Number() with { Name = "n" + i }).ToArray()));
        Assert.Throws<ArgumentException>(() => Schema(Number(), Number()));
        var schema = Schema(new ParameterDefinition("text", "文字", ParameterType.Text) { MaximumLength = 65536 });
        Assert.Equal("parameters.size", Assert.Single(schema.Validate(JsonSerializer.SerializeToElement(new { text = new string('中', 65536) }))).Code);
    }

    [Fact]
    public void BuiltInConnectionsUseTheSameDeclaredBoundsAsMetadata()
    {
        Assert.Equal("60000", ModbusConnectionParameters.Schema.Parameters.Single(f => f.Name == "readTimeoutMs").MaximumJson);
        Assert.Throws<ArgumentException>(() => new ModbusConnectionSettings { ConnectionId = 1, Host = "localhost", ReadTimeoutMs = 60001 }.Validate());
        Assert.Throws<ArgumentException>(() => new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", PingIntervalMs = 999 }.ValidateConnection());
        Assert.Equal(60000, new ModbusConnectionSettings { ConnectionId = 1, Host = "localhost", ReadTimeoutMs = 60000 }.Validate().ReadTimeoutMs);
        Assert.Equal(1000, new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", PingIntervalMs = 1000 }.ValidateConnection().PingIntervalMs);
    }
}

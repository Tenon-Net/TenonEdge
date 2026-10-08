using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class FieldMappingConfigurationTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("[{\"target\":\"x\",\"type\":1,\"sourceField\":\"x\"}]")]
    [InlineData("[{\"target\":\"x\",\"type\":\"integer\",\"constant\":null}]")]
    [InlineData("[{\"target\":\"x\",\"type\":\"integer\",\"sourceField\":\"x\",\"constant\":1}]")]
    [InlineData("[{\"target\":\"x\",\"type\":\"integer\",\"sourceField\":\"x\",\"script\":\"arbitrary\"}]")]
    public void InvalidAndAmbiguousRulesAreRejected(string json)
    {
        using var config = JsonDocument.Parse(json);
        var error = Record.Exception(() => FieldMappingConfiguration.Parse(config.RootElement));
        Assert.True(error is JsonException or ArgumentException);
    }

    [Fact]
    public void ConfiguredConstantsUseExactTypesAndApplyArithmeticOnce()
    {
        using var config = JsonDocument.Parse("""[{"target":"n","type":"integer","constant":9223372036854775807},{"target":"scaled","type":"number","constant":2,"scale":3,"offset":1}]""");
        var mapping = FieldMappingConfiguration.Parse(config.RootElement)!;
        var at = DateTimeOffset.UnixEpoch;
        var source = new EdgeMessage(Guid.NewGuid(), "input", "device", at, TimeSource.Gateway, at, []);
        var result = mapping.Apply(source).Message!;
        Assert.Equal("9223372036854775807", result.Values["n"].ToString());
        Assert.Equal("7", result.Values["scaled"].ToString());
    }
}

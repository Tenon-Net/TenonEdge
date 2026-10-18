using System.Text.Json;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.UnitTests.Connections;

public sealed class ModuleConnectionDraftTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"nested\":{}}")]
    [InlineData("{\"array\":[]}")]
    [InlineData("{\"value\":1,\"value\":2}")]
    [InlineData("{\"invalid/key\":1}")]
    public void InvalidScalarShapeIsRejectedWithoutACatalog(string parameters) => Assert.Throws<ArgumentException>(() =>
        Normalize(new("testwire", 1, JsonSerializer.Deserialize<JsonElement>(parameters))));

    [Theory]
    [InlineData("http", 1)]
    [InlineData("mqtt", 1)]
    [InlineData("modbus", 1)]
    [InlineData("tenon", 1)]
    [InlineData("testwire", 0)]
    [InlineData("test-wire", 1)]
    public void InvalidTypeOrVersionIsRejected(string kind, int version) => Assert.Throws<ArgumentException>(() =>
        Normalize(new(kind, version, JsonSerializer.Deserialize<JsonElement>("{}"))));

    [Fact]
    public void ShapeCheckPreservesExactValuesAndDoesNotRequireInstallation()
    {
        using var json = JsonDocument.Parse("{\"value\":9007199254740993,\"scale\":1.00E2}");
        var draft = Normalize(new("uninstalled", 3, json.RootElement));
        json.Dispose();
        Assert.Equal("9007199254740993", draft.Module!.Parameters.GetProperty("value").GetRawText());
        Assert.Equal("1.00E2", draft.Module.Parameters.GetProperty("scale").GetRawText());
        Assert.True(draft.SameKind(draft with { Module = draft.Module with { Version = 4 } }));
        Assert.False(draft.SameKind(draft with { Module = draft.Module with { Kind = "different" } }));
    }

    [Fact]
    public void MissingOversizedOrMixedParametersCannotEnterDraft()
    {
        Assert.Throws<ArgumentException>(() => Normalize(new("testwire", 1, default)));
        Assert.Throws<ArgumentException>(() => Normalize(new("testwire", 1,
            JsonSerializer.SerializeToElement(new { text = new string('x', 128 * 1024) }))));
        Assert.Throws<ArgumentException>(() => Normalize(new("testwire", 1,
            JsonSerializer.SerializeToElement(Enumerable.Range(0, 129).ToDictionary(i => "p" + i, i => i)))));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1,
            new("v", "wire", "Wire", ConnectionKind.Module, Http: new("http://localhost/"),
                Module: new("testwire", 1, JsonSerializer.Deserialize<JsonElement>("{}")))));
    }

    private static ConnectionDraft Normalize(ModuleConnectionDraft module) => ConnectionDraftValidation.Normalize(1,
        new("v", "wire", "Wire", ConnectionKind.Module, Module: module));
}

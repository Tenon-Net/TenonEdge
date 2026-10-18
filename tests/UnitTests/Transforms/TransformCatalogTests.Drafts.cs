using System.Text.Json;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.UnitTests.Transforms;

public sealed partial class TransformCatalogTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"offset\":null}")]
    [InlineData("{\"offset\":1.5}")]
    [InlineData("{\"offset\":101}")]
    [InlineData("{\"offset\":\"incomplete\"}")]
    public void DraftMayBeIncompleteButCannotCompileAndNeverInvokesTheFactory(string json)
    {
        var factory = new OffsetFactory();
        var catalog = new TransformCatalog([factory]);
        var config = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(json, catalog.NormalizeDraft(factory.Kind, 1, config).GetRawText());
        Assert.Equal(0, factory.Builds);
        Assert.Throws<ArgumentException>(() => catalog.Build(factory.Kind, 1, config));
        Assert.Equal(0, factory.Builds);
    }

    [Fact]
    public void NewVersionOfMappingKindUsesInstalledDeclarationNotVersionOneRulesAndSurvivesStructuralRead()
    {
        var factory = new OffsetFactory { Kind = "transform.map", Version = 2 };
        var catalog = new TransformCatalog([factory]);
        var config = JsonSerializer.Deserialize<JsonElement>("{\"offset\":1}");
        var graph = new RouteGraph(1, [new("custom", factory.Kind, 2, "Version two", config)], [], new([]));
        Assert.Equal(config.GetRawText(), RouteGraphFormat.Normalize(graph, catalog).Nodes[0].Config.GetRawText());
        Assert.Equal(config.GetRawText(), RouteGraphFormat.NormalizeStored(graph).Nodes[0].Config.GetRawText());
        Assert.Throws<ArgumentException>(() => RouteGraphFormat.Normalize(graph));
        Assert.Equal("9007199254740994", catalog.Build(factory.Kind, 2, config).Apply(Message()).Message!.Values["value"].ToString());
        Assert.Equal(2, catalog.Modules.Count(module => module.Kind == "transform.map"));
    }

    [Fact]
    public void StoredCustomConfigurationRetainsExactNumbersAndRejectsNestedDuplicateKeys()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{\"precise\":9007199254740993,\"decimal\":1.2300e+20}");
        Assert.Equal("{\"decimal\":1.2300e+20,\"precise\":9007199254740993}", TransformCatalog.NormalizeStoredConfiguration(json).GetRawText());
        Assert.Throws<ArgumentException>(() => TransformCatalog.NormalizeStoredConfiguration(JsonSerializer.Deserialize<JsonElement>("{\"nested\":{\"x\":1,\"x\":2}}")));
    }
    [Theory]
    [InlineData("connectionId")]
    [InlineData("sourceIds")]
    public void ScalarParameterNamesAreNotMistakenForCrossHostResourceReferences(string name)
    {
        var factory = new OffsetFactory
        {
            Parameters = new("scalar", ParameterScope.Transform,
            [new(name, "Ordinary scalar", ParameterType.Number)])
        };
        var catalog = new TransformCatalog([factory]);
        var config = JsonSerializer.Deserialize<JsonElement>($"{{\"{name}\":9007199254740993}}");
        var file = new PortableConfiguration(1, [], [], [], [new("example", "Example", null, false,
            new(1, [new("custom", factory.Kind, 1, "Custom", config)], [], new([])))]);
        var plan = ConfigurationImportPlanner.Prepare(file, new ConfigurationCheckpoint("0", [], [], [], []),
            ConfigurationImportMode.Replace, () => 1, TestContext.Current.CancellationToken, catalog);
        var exported = ConfigurationExport.Create(plan.Draft, new Dictionary<long, string>(), TestContext.Current.CancellationToken);
        Assert.Equal("9007199254740993", Assert.Single(exported.Routes).Graph.Nodes[0].Config.GetProperty(name).GetRawText());
        Assert.Equal(0, factory.Builds);
    }

}

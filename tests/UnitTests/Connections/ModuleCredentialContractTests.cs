using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Connections;

public sealed class ModuleCredentialContractTests
{
    private static ParameterSchema Schema => new("connection.authwire", ParameterScope.Connection,
        [new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 255 },
         new("port", "Port", ParameterType.Number) { Required = true, WholeNumber = true },
         new("timeout", "Timeout", ParameterType.Number),
         new("auth", "Auth", ParameterType.Credential) { Required = true }]);
    private static ConnectionModuleDefinition Definition => new("authwire", 1, "Wire", Schema, ["host", "port"]);
    private static JsonElement Parameters => JsonSerializer.SerializeToElement(new { host = "localhost", port = 123, timeout = 1000 });

    [Theory]
    [InlineData("missing")]
    [InlineData("auth")]
    [InlineData("timeout")]
    public void RecipientBindingMustSelectDeclaredRequiredNonsecretScalars(string name) =>
        Assert.Throws<ArgumentException>(() => new ConnectionModuleDefinition("authwire", 1, "Wire", Schema, [name]));

    [Fact]
    public void BindingIsNotSharedConfigurationIdentityAndIncludesExplicitTypeVersion()
    {
        var definition = Definition;
        var recipient = definition.RecipientIdentity(Parameters);
        Assert.Equal(recipient, definition.RecipientIdentity(JsonSerializer.SerializeToElement(new { host = "localhost", port = 123, timeout = 2000 })));
        Assert.NotEqual(recipient, definition.RecipientIdentity(JsonSerializer.SerializeToElement(new { host = "localhost", port = 124, timeout = 1000 })));
        Assert.NotEqual(recipient, new ConnectionModuleDefinition("authwire", 2, "Wire", Schema, ["host", "port"]).RecipientIdentity(Parameters));
        Assert.Throws<ArgumentException>(() => new ConnectionModuleDefinition("authwire", 1, "Wire", Schema, ["host", "host"]));
    }

    [Fact]
    public void NonsecretDraftCanBeFrozenButRequiredMaterialCannotBeForgedOrSkippedAtUse()
    {
        var catalog = new ConnectionModuleCatalog([Definition]);
        var frozen = catalog.Freeze(1, "authwire", 1, Parameters);
        Assert.Throws<ArgumentException>(() => catalog.ValidateForUse(frozen, null));
        Assert.Throws<ArgumentException>(() => catalog.Freeze(1, "authwire", 1,
            JsonSerializer.SerializeToElement(new { host = "localhost", port = 123, auth = "fixture-secret" })));
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([new("authwire", 1, "Wire", Schema)])
            .Freeze(1, "authwire", 1, Parameters)); // No binding declaration is not credential support.
    }

    [Fact]
    public void CredentialValueDoesNotSerializeOrFormatItsSecrets()
    {
        var secrets = new Dictionary<string, string> { ["auth"] = "fixture-secret" };
        var credential = new ModuleCredential(new("authwire", 1, "sha256:fixture", "user", "marker", "version"), secrets);
        secrets["auth"] = "changed";
        Assert.Equal("fixture-secret", credential.GetSecret("auth"));
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(credential), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", credential.ToString(), StringComparison.Ordinal);
        Assert.Equal("auth", Assert.Single(credential.Names));
    }

    [Fact]
    public void OptionalCredentialMayBeAbsentButExistingUnusableMaterialCannotBecomeAnonymous()
    {
        var schema = new ParameterSchema(Schema.Id, Schema.Scope, Schema.Parameters.Select(field => field with { Required = field.Type == ParameterType.Credential ? false : field.Required }));
        var catalog = new ConnectionModuleCatalog([new("authwire", 1, "Wire", schema, ["host", "port"])]);
        var frozen = catalog.Freeze(1, "authwire", 1, Parameters);
        catalog.ValidateForUse(frozen, new Resolver(null, null));
        var identity = new ModuleCredentialIdentity("authwire", 1, Definition.RecipientIdentity(Parameters), "user", "marker", "version");
        Assert.Throws<ArgumentException>(() => catalog.ValidateForUse(frozen, new Resolver(identity, null)));
        catalog.ValidateForUse(frozen, new Resolver(identity, new(identity, new Dictionary<string, string> { ["auth"] = "fixture" })));
    }

    private sealed class Resolver(ModuleCredentialIdentity? identity, ModuleCredential? value) : IModuleCredentialResolver
    {
        public ModuleCredentialIdentity? DescribeModule(long connectionId) => identity;
        public ModuleCredential? ResolveModule(ModuleConnectionSnapshot connection) => value;
    }
}

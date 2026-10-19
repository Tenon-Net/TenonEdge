using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.UnitTests.Outputs;

public sealed class OutputTransportRouteTests
{
    private static JsonElement Configuration(string address, string recipient = "wire://localhost:19001") =>
        JsonSerializer.SerializeToElement(new { transportRoute = new { recipient, addressTemplate = address } });

    [Fact]
    public void AbsentRouteIsReadableForHistoryButCannotBeRequiredForNewBindings()
    {
        using var empty = JsonDocument.Parse("{}");
        Assert.Null(OutputTransportRoute.Read(empty.RootElement, "output.wire"));
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(empty.RootElement, "output.wire", required: true));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"value\"")]
    public void NonObjectConfigurationUsesTheNormalValidationFailure(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(document.RootElement, "output.wire"));
    }

    [Theory]
    [InlineData("output.http", "http://localhost:19001")]
    [InlineData("output.mqtt", "mqtt://localhost:19001")]
    [InlineData("output.tenon", "tenon://localhost:19001")]
    [InlineData("input.wire", "wire://localhost:19001")]
    [InlineData("output.wire", "tcp://localhost:19001")]
    [InlineData("output.wire", "wire://localhost")]
    [InlineData("output.wire", "wire://user:secret@localhost:19001")]
    [InlineData("output.wire", "wire://localhost:19001?token=secret")]
    [InlineData("output.wire", "wire://localhost:19001#fragment")]
    public void RecipientMustBeExplicitAndBoundToAnExtensionProtocol(string kind, string recipient) =>
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(Configuration("events", recipient), kind));

    [Theory]
    [InlineData("{payload}")]
    [InlineData("{deviceKey")]
    [InlineData("deviceKey}")]
    [InlineData("{{deviceKey}}")]
    [InlineData("events\n/{deviceKey}")]
    [InlineData("events\0")]
    public void OnlyDeviceKeySubstitutionAndNoControlCharactersAreAccepted(string address) =>
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(Configuration(address), "output.wire"));

    [Theory]
    [InlineData("{\"recipient\":\"wire://localhost:19001\",\"addressTemplate\":\"events\",\"extra\":true}")]
    [InlineData("{\"recipient\":\"wire://localhost:19001\",\"addressTemplate\":\"events\",\"addressTemplate\":\"other\"}")]
    [InlineData("{\"recipient\":null,\"addressTemplate\":\"events\"}")]
    [InlineData("{\"recipient\":\"wire://localhost:19001\",\"addressTemplate\":1}")]
    [InlineData("[]")]
    public void AmbiguousOrNonTextFrozenDescriptionsAreRejected(string route)
    {
        using var document = JsonDocument.Parse("{\"transportRoute\":" + route + "}");
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(document.RootElement, "output.wire"));
    }

    [Fact]
    public void ByteLimitIncludesEveryWorstCaseDeviceKeyExpansion()
    {
        var route = OutputTransportRoute.Read(Configuration(new string('界', 1322) + "a{deviceKey}/{deviceKey}"), "output.wire")!;
        var key = "m" + new string('x', 63);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(route.AddressFor(key)));
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(Configuration(new string('界', 1322) + "ab{deviceKey}/{deviceKey}"), "output.wire"));
        Assert.Throws<ArgumentException>(() => route.AddressFor("invalid/key"));
    }

    [Fact]
    public void ConstantAndRepeatedTemplateAddressesAreExpandedWithoutReadingPayload()
    {
        Assert.Equal("", OutputTransportRoute.Read(Configuration(""), "output.wire")!.AddressFor("meter"));
        Assert.Equal("events/meter-1/meter-1", OutputTransportRoute.Read(Configuration("events/{deviceKey}/{deviceKey}"), "output.wire")!.AddressFor("meter-1"));
        Assert.Equal(4096, OutputTransportRoute.Read(Configuration(new string('x', 4096)), "output.wire")!.AddressFor("meter").Length);
        Assert.Throws<ArgumentException>(() => OutputTransportRoute.Read(Configuration(new string('x', 4097)), "output.wire"));
    }
}

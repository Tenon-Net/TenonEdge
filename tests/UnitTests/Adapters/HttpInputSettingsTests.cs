using System.Text.Json;
using TenonEdge.Adapters.Http;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpInputSettingsTests
{
    [Theory]
    [InlineData("\"periodMs\":999")]
    [InlineData("\"timeoutMs\":0")]
    [InlineData("\"path\":\"../escape\"")]
    [InlineData("\"secret\":\"do-not-store\"")]
    [InlineData("\"headers\":{\"Authorization\":\"secret\"}")]
    public void InvalidOrSecretConfigurationIsRejected(string member)
    {
        using var json = JsonDocument.Parse("{\"connectionId\":1,\"baseAddress\":\"http://localhost/api/\",\"fields\":[]," + member + "}");
        Assert.ThrowsAny<Exception>(() => HttpInputSettings.Parse(json.RootElement));
    }

    [Fact]
    public void InputAndOutputShareAddressNormalizationAndHeaderRules()
    {
        using var json = JsonDocument.Parse("""{"connectionId":1,"baseAddress":"HTTP://LOCALHOST:80/api","path":"/values?fixed=1","fields":[],"headers":{"X-Static":"fixed"}}""");
        var settings = HttpInputSettings.Parse(json.RootElement);
        Assert.Equal("http://localhost/api/", settings.BaseAddress);
        Assert.Equal("http://localhost/api/values?fixed=1", settings.Endpoint.AbsoluteUri);
        Assert.Equal("fixed", settings.Headers["x-static"]);
    }
}

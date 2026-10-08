using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Mapping;

public sealed class JsonFieldPointerTests
{
    private const string Example = """{"foo":["bar","baz"],"":0,"a/b":1,"c%d":2,"e^f":3,"g|h":4,"i\\j":5,"k\"l":6," ":7,"m~n":8,"~1":9,"01":10,"big":18446744073709551615,"null":null}""";

    [Theory]
    [InlineData("/foo/0", "\"bar\"")]
    [InlineData("/", "0")]
    [InlineData("/a~1b", "1")]
    [InlineData("/c%d", "2")]
    [InlineData("/e^f", "3")]
    [InlineData("/g|h", "4")]
    [InlineData("/i\\j", "5")]
    [InlineData("/k\"l", "6")]
    [InlineData("/ ", "7")]
    [InlineData("/m~0n", "8")]
    [InlineData("/~01", "9")]
    [InlineData("/01", "10")]
    [InlineData("/big", "18446744073709551615")]
    [InlineData("/null", "null")]
    public void ResolvesExactTokensWithoutNumericOrUriReinterpretation(string path, string expected)
    {
        using var document = JsonDocument.Parse(Example);
        Assert.True(new JsonFieldPointer(path).TryResolve(document.RootElement, out var value));
        Assert.Equal(expected, value.GetRawText());
    }

    [Fact]
    public void EmptyPointerSelectsEntireDocumentIncludingScalar()
    {
        using var document = JsonDocument.Parse("42");
        Assert.True(new JsonFieldPointer("").TryResolve(document.RootElement, out var value));
        Assert.Equal("42", value.GetRawText());
        Assert.False(new JsonFieldPointer("").TryResolve(default, out _));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("#/foo")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/~1~")]
    public void InvalidSyntaxIsAConfigurationError(string path) => Assert.Throws<ArgumentException>(() => new JsonFieldPointer(path));

    [Theory]
    [InlineData("/foo/1\0")]
    [InlineData("/foo/١")]
    [InlineData("/foo/-")]
    [InlineData("/foo/01")]
    [InlineData("/foo/+1")]
    [InlineData("/foo/-1")]
    [InlineData("/foo/ 1")]
    [InlineData("/foo/1 ")]
    [InlineData("/foo/")]
    [InlineData("/foo/2")]
    [InlineData("/foo/999999999999999999")]
    [InlineData("/foo/0/x")]
    [InlineData("/missing")]
    [InlineData("/Foo")]
    [InlineData("/null/x")]
    [InlineData("/c%25d")]
    public void MissingOrInvalidTraversalDoesNotReturnOldValues(string path)
    {
        using var document = JsonDocument.Parse(Example);
        Assert.False(new JsonFieldPointer(path).TryResolve(document.RootElement, out var value));
        Assert.Equal(JsonValueKind.Undefined, value.ValueKind);
    }

    [Fact]
    public void DuplicateSelectedMemberIsAmbiguousAndIndependentCallsHaveNoSharedResult()
    {
        var path = new JsonFieldPointer("/value");
        using var first = JsonDocument.Parse("{\"value\":1}");
        using var duplicate = JsonDocument.Parse("{\"value\":1,\"value\":2}");
        using var missing = JsonDocument.Parse("{}");
        Assert.True(path.TryResolve(first.RootElement, out var value));
        Assert.False(path.TryResolve(duplicate.RootElement, out _));
        Assert.False(path.TryResolve(missing.RootElement, out _));
        Assert.Equal(1, value.GetInt32());
    }
}

using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.UnitTests.Connections;

public sealed class ConnectionDraftValidationTests
{
    [Fact]
    public void ProtocolDefaultsAndCanonicalFormsUseRuntimeValidators()
    {
        var modbus = ConnectionDraftValidation.Normalize(1, Request());
        Assert.Equal("plc.example", modbus.Modbus!.Host);
        Assert.Equal(502, modbus.Modbus.Port);
        var mqtt = ConnectionDraftValidation.Normalize(2, Request() with { Kind = ConnectionKind.Mqtt, Modbus = null, Mqtt = new("[2001:DB8::1]") });
        Assert.Equal("2001:db8::1", mqtt.Mqtt!.Host);
        var http = ConnectionDraftValidation.Normalize(3, Request() with { Kind = ConnectionKind.Http, Modbus = null, Http = new("https://EXAMPLE.com/api") });
        Assert.Equal("https://example.com/api/", http.Http!.BaseAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_invalid")]
    [InlineData("中文")]
    [InlineData("has space")]
    [InlineData("line\n")]
    public void InvalidKeysAreRejected(string key) => Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Key = key }));

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("X-Access-Token")]
    [InlineData("my_password")]
    [InlineData("X-Secret")]
    public void SecretHeadersCannotEnterDraft(string name) => Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1,
        Request() with { Kind = ConnectionKind.Http, Modbus = null, Http = new("https://example.test", Headers: new() { [name] = "not-a-real-secret" }) }));

    [Theory]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test/?token=example")]
    [InlineData("file:///tmp/example")]
    public void AddressAuthenticationAndQueryAreRejected(string address) => Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1,
        Request() with { Kind = ConnectionKind.Http, Modbus = null, Http = new(address) }));

    [Fact]
    public void IdentityAndAllProtocolParameterBoundariesAreChecked()
    {
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = null }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Mqtt = new("example.test") }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Kind = ConnectionKind.Http }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { ExpectedVersion = null! }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Name = "\t" }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Key = new string('a', 65) }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = new("example.test", Port: 0) }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = new("example.test", Format: (ModbusFrameFormat)999) }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = new("example.test", ReadTimeoutMs: 99) }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = new("example.test", RequestIntervalMs: -1) }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = new("example.test", RtuSilenceMs: 0) }));
        Assert.Throws<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = null, Kind = ConnectionKind.Mqtt, Mqtt = new("example.test ") }));
        Assert.ThrowsAny<ArgumentException>(() => ConnectionDraftValidation.Normalize(1, Request() with { Modbus = null, Kind = ConnectionKind.Mqtt, Mqtt = new("example.test", PingIntervalMs: 999) }));
    }

    private static ConnectionDraftRequest Request() => new("0", "plc-1", "PLC", ConnectionKind.Modbus, Modbus: new("PLC.EXAMPLE"));
}

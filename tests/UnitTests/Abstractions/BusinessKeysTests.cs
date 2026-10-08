using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Abstractions;

/// <summary>
/// 业务标识的字符规则（T1-02；总设计 §6.5）：ASCII 字母、数字和 -、_、.，长度 1—64，首字符为字母或数字。
/// 设备标识与输入标识进入消息前按此校验，因为它们会拼进 MQTT Topic。
/// </summary>
public sealed class BusinessKeysTests
{
    [Theory]
    [InlineData("meter-001")]
    [InlineData("M")]
    [InlineData("0.a_b-c")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")] // 64 个字符
    public void ValidKeys(string key) => Assert.True(BusinessKeys.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-meter")]
    [InlineData(".meter")]
    [InlineData("meter 1")]
    [InlineData("meter/1")] // 会被当作 Topic 层级
    [InlineData("meter+1")]
    [InlineData("meter#")]
    [InlineData("电表")]
    [InlineData("meter-1\n")]
    [InlineData("a2345678901234567890123456789012345678901234567890123456789012345")] // 65 个字符
    public void InvalidKeys(string? key) => Assert.False(BusinessKeys.IsValid(key));

    [Theory]
    [InlineData("meter 1", "simulated")]
    [InlineData("meter-1", "sim/1")]
    [InlineData("", "simulated")]
    public void MessagesRejectInvalidDeviceAndInputKeys(string deviceKey, string inputKey)
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => new EdgeMessage(Guid.CreateVersion7(), inputKey, deviceKey, now, TimeSource.Gateway, now, []));
    }
}

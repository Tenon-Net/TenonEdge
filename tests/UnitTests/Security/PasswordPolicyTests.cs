using TenonEdge.Host.Security;

namespace TenonEdge.UnitTests.Security;

/// <summary>密码规则（总设计 §3.3，功能需求 §1.4、§1.7）。</summary>
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("Tenon-Edge-2026")]
    [InlineData("gateway2026")]
    [InlineData("网关密码2026ab")]
    public void StrongPasswordsAreAccepted(string password)
    {
        Assert.Null(PasswordPolicy.Validate(password, "admin"));
    }

    [Theory]
    [InlineData("abc1234", "至少 8 位")]
    [InlineData("12345678", "同时包含字母和数字")]
    [InlineData("onlyletters", "同时包含字母和数字")]
    [InlineData("Admin123", "过于常见")]
    [InlineData("QWERTY123", "过于常见")]
    [InlineData("operator1", "不能与账号相同")]
    public void WeakPasswordsAreRejectedWithAReason(string password, string reason)
    {
        var message = PasswordPolicy.Validate(password, "Operator1");

        Assert.NotNull(message);
        Assert.Contains(reason, message, StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordsLongerThanTheLimitAreRejected()
    {
        var password = "a1" + new string('x', PasswordPolicy.MaximumLength - 1);

        Assert.Contains("最多", PasswordPolicy.Validate(password, "admin"), StringComparison.Ordinal);
        Assert.Null(PasswordPolicy.Validate(password[..PasswordPolicy.MaximumLength], "admin"));
    }
}

using TenonEdge.Host.Security;

namespace TenonEdge.UnitTests.Security;

/// <summary>账号名规则（功能需求 §1.3）：字母、数字和 -、_、.，3—32 位；比较时不区分大小写。</summary>
public sealed class AccountNamesTests
{
    [Theory]
    [InlineData("admin", true)]
    [InlineData("ops.team-1_a", true)]
    [InlineData("abc", true)]
    [InlineData("a234567890123456789012345678901b", true)]
    [InlineData("ab", false)]
    [InlineData("a2345678901234567890123456789012c", false)]
    [InlineData("has space", false)]
    [InlineData("管理员", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheAllowedCharactersAndLengthsAreValid(string? name, bool valid)
    {
        Assert.Equal(valid, AccountNames.IsValid(name));
    }

    [Fact]
    public void NamesDifferingOnlyInCaseNormalizeToTheSameAccount()
    {
        Assert.Equal(AccountNames.Normalize("Admin"), AccountNames.Normalize("admin"));
    }
}

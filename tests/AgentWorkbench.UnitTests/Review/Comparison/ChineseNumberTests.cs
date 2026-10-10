using AgentWorkbench.Review.Comparison;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Comparison;

public sealed class ChineseNumberTests
{
    private static long? Parse(string input) => ChineseNumber.TryParse(input, out var value) ? value : null;

    [Theory]
    [InlineData("壹拾贰万捌仟元整", 128000L)]
    [InlineData("壹仟伍佰", 1500L)]
    [InlineData("叁佰", 300L)]
    [InlineData("十", 10L)]
    [InlineData("拾", 10L)]
    [InlineData("壹", 1L)]
    [InlineData("两", 2L)]
    [InlineData("壹亿", 100000000L)]
    [InlineData("壹佰万元", 1000000L)]
    [InlineData("壹万贰仟叁佰肆拾伍元", 12345L)]
    [InlineData("壹仟零伍", 1005L)]
    [InlineData("人民币壹万捌仟元整", 18000L)]
    public void TryParse_ChineseAmounts_ReturnsExpectedValue(string input, long expected)
    {
        Assert.Equal(expected, Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("零")]
    [InlineData("abc")]
    [InlineData("2026")]
    public void TryParse_InvalidOrZeroInput_ReturnsFalse(string input)
    {
        Assert.False(ChineseNumber.TryParse(input, out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryParse_NullInput_ReturnsFalse()
    {
        Assert.False(ChineseNumber.TryParse(null!, out var value));
        Assert.Equal(0, value);
    }
}

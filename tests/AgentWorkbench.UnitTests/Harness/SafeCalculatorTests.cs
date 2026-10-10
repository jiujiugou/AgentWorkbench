using AgentWorkbench.AgentHarness.Tools;
using Xunit;

namespace AgentWorkbench.UnitTests.Harness;

public sealed class SafeCalculatorTests
{
    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("10/4", 2.5)]
    [InlineData("2*(3+4)-5", 9)]
    [InlineData("-3+10", 7)]
    public void Evaluate_BasicExpressions(string expression, double expected)
        => Assert.Equal(expected, SafeCalculator.Evaluate(expression), 6);

    [Theory]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("abc")]
    [InlineData("1/0")]
    public void Evaluate_Invalid_Throws(string expression)
        => Assert.ThrowsAny<Exception>(() => SafeCalculator.Evaluate(expression));

    [Fact]
    public void Evaluate_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => SafeCalculator.Evaluate("   "));
}

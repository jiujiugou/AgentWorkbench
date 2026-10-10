using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Comparison;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Comparison;

public sealed class ValueConsistencyCheckerTests
{
    private readonly ValueConsistencyChecker _checker = new();

    [Fact]
    public void Check_ValueRuleMatch_NoFinding()
    {
        var rules = new[] { Rule("总价", "amount", "128000") };
        var elements = new[] { Element("amount", "128000") };

        Assert.Empty(_checker.Check(Context(), rules, elements));
    }

    [Fact]
    public void Check_ValueRuleMismatch_ProducesFinding()
    {
        var rules = new[] { Rule("总价", "amount", "100000") };
        var elements = new[] { Element("amount", "128000") };

        var finding = Assert.Single(_checker.Check(Context(), rules, elements));

        Assert.Equal("取值不一致", finding.Category);
        Assert.Equal("100000", finding.Expected);
        Assert.Equal("128000", finding.Actual);
        Assert.Equal(1L, finding.ChunkId!.Value);
        Assert.Equal(0, finding.Seq!.Value);
    }

    [Fact]
    public void Check_ValueRuleWithoutTargetKey_Ignored()
    {
        var rules = new[] { new RuleRecord { Id = 1, RuleType = RuleTypes.ValueRule, Name = "x", Expected = "1" } };

        Assert.Empty(_checker.Check(Context(), rules, new[] { Element("amount", "2") }));
    }

    [Fact]
    public void Check_ValueRuleWithoutExpected_Ignored()
    {
        var rules = new[] { Rule("x", "amount", null) };

        Assert.Empty(_checker.Check(Context(), rules, new[] { Element("amount", "2") }));
    }

    [Fact]
    public void Check_AmountAndChineseText_Consistent_NoFinding()
    {
        var elements = new[] { Element("amount", "128000"), Element("amount_text", "壹拾贰万捌仟元整") };

        Assert.Empty(_checker.Check(Context(), Array.Empty<RuleRecord>(), elements));
    }

    [Fact]
    public void Check_AmountAndChineseText_Inconsistent_ProducesHighFinding()
    {
        var elements = new[] { Element("amount", "128000"), Element("amount_text", "壹拾贰万元整") };

        var finding = Assert.Single(_checker.Check(Context(), Array.Empty<RuleRecord>(), elements));

        Assert.Equal("金额不一致", finding.Category);
        Assert.Equal(FindingSeverities.High, finding.Severity);
        Assert.Null(finding.RuleId);
        Assert.Equal("128000", finding.Expected);
        Assert.Equal("120000", finding.Actual);
    }

    [Fact]
    public void Check_AmountWithoutChineseText_NoFinding()
    {
        var elements = new[] { Element("amount", "128000") };

        Assert.Empty(_checker.Check(Context(), Array.Empty<RuleRecord>(), elements));
    }

    [Fact]
    public void Check_AmountNonNumeric_NoFinding()
    {
        var elements = new[] { Element("amount", "约十二万"), Element("amount_text", "壹拾贰万元整") };

        Assert.Empty(_checker.Check(Context(), Array.Empty<RuleRecord>(), elements));
    }

    private static ReviewContext Context() => new("doc", "f.pdf", Array.Empty<ChunkRecord>());

    private static RuleRecord Rule(string name, string targetKey, string? expected)
        => new() { Id = 1, Name = name, RuleType = RuleTypes.ValueRule, TargetKey = targetKey, Expected = expected, Severity = FindingSeverities.Medium, Body = "body" };

    private static ElementDraft Element(string key, string value)
        => new(key, key, value, "text", null, "rule", 0.9, 1, 0, 1, 1, "ev");
}

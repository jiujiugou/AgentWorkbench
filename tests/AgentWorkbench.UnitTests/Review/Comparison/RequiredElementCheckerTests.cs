using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Comparison;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Comparison;

public sealed class RequiredElementCheckerTests
{
    private readonly RequiredElementChecker _checker = new();

    [Fact]
    public void Check_RequiredElementPresent_NoFinding()
    {
        var rules = new[] { Rule("amount", RuleTypes.ElementRequired) };
        var elements = new[] { Element("amount", "128000") };

        var findings = _checker.Check(Context(), rules, elements).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_RequiredElementMissing_ProducesFinding()
    {
        var rules = new[] { Rule("合同总价", RuleTypes.ElementRequired, targetKey: "amount", severity: FindingSeverities.High, expected: "100") };

        var finding = Assert.Single(_checker.Check(Context(), rules, Array.Empty<ElementDraft>()));

        Assert.Equal(FindingSeverities.High, finding.Severity);
        Assert.Equal("要素缺失", finding.Category);
        Assert.Contains("合同总价", finding.Title);
        Assert.Equal("100", finding.Expected);
        Assert.Equal(rules[0].Id, finding.RuleId);
        Assert.Null(finding.Evidence);
        Assert.Null(finding.Seq);
    }

    [Fact]
    public void Check_ElementPresentButValueWhitespace_TreatedAsMissing()
    {
        var rules = new[] { Rule("合同总价", RuleTypes.ElementRequired, targetKey: "amount") };
        var elements = new[] { Element("amount", "   ") };

        Assert.Single(_checker.Check(Context(), rules, elements));
    }

    [Fact]
    public void Check_TargetKeyNull_UsesRuleNameAsKey()
    {
        var rules = new[] { Rule("出卖人", RuleTypes.ElementRequired) };

        Assert.Empty(_checker.Check(Context(), rules, new[] { Element("出卖人", "甲公司") }));
        Assert.Single(_checker.Check(Context(), rules, Array.Empty<ElementDraft>()));
    }

    [Fact]
    public void Check_NonElementRequiredRule_Ignored()
    {
        var rules = new[] { Rule("违约", RuleTypes.ClauseRequired) };

        Assert.Empty(_checker.Check(Context(), rules, Array.Empty<ElementDraft>()));
    }

    private static ReviewContext Context() => new("doc", "f.pdf", Array.Empty<ChunkRecord>());

    private static RuleRecord Rule(string name, string type, string? targetKey = null, string severity = FindingSeverities.Medium, string? expected = null)
        => new() { Id = 1, Name = name, RuleType = type, TargetKey = targetKey, Severity = severity, Expected = expected, Body = "body" };

    private static ElementDraft Element(string key, string value)
        => new(key, key, value, "text", null, "rule", 0.9, null, null, null, null, null);
}

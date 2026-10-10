using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Comparison;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Comparison;

public sealed class ClausePresenceCheckerTests
{
    private readonly ClausePresenceChecker _checker = new();

    [Fact]
    public void Check_RequiredClauseKeywordPresent_NoFinding()
    {
        var rules = new[] { Rule("违约责任", RuleTypes.ClauseRequired, matchHint: "违约") };

        Assert.Empty(_checker.Check(Context(Chunk("双方应承担违约责任。")), rules, Array.Empty<ElementDraft>()));
    }

    [Fact]
    public void Check_RequiredClauseKeywordAbsent_ProducesFinding()
    {
        var rules = new[] { Rule("违约责任", RuleTypes.ClauseRequired, matchHint: "违约") };

        var finding = Assert.Single(_checker.Check(Context(Chunk("本合同无相关条款。")), rules, Array.Empty<ElementDraft>()));

        Assert.Equal("条款缺失", finding.Category);
        Assert.Contains("违约责任", finding.Title);
        Assert.Null(finding.Seq);
    }

    [Fact]
    public void Check_BlankMatchHint_FallsBackToRuleName()
    {
        var rules = new[] { Rule("争议解决", RuleTypes.ClauseRequired, matchHint: "  ") };

        Assert.Empty(_checker.Check(Context(Chunk("争议解决方式为仲裁。")), rules, Array.Empty<ElementDraft>()));
    }

    [Fact]
    public void Check_ForbiddenClausePresent_ProducesFindingWithEvidence()
    {
        var rules = new[] { Rule("禁止转租", RuleTypes.ClauseForbidden, matchHint: "转租") };

        var finding = Assert.Single(_checker.Check(Context(Chunk("承租人不得擅自转租该房屋。")), rules, Array.Empty<ElementDraft>()));

        Assert.Equal("风险条款", finding.Category);
        Assert.Equal("转租", finding.Actual);
        Assert.Equal(7L, finding.ChunkId!.Value);
        Assert.Equal(1, finding.Seq!.Value);
        Assert.Equal(1, finding.PageFrom!.Value);
        Assert.Equal(2, finding.PageTo!.Value);
        Assert.NotNull(finding.Evidence);
        Assert.Contains("转租", finding.Evidence);
    }

    [Fact]
    public void Check_ForbiddenClauseAbsent_NoFinding()
    {
        var rules = new[] { Rule("禁止转租", RuleTypes.ClauseForbidden, matchHint: "转租") };

        Assert.Empty(_checker.Check(Context(Chunk("本合同无该项。")), rules, Array.Empty<ElementDraft>()));
    }

    [Fact]
    public void Check_ElementRequiredRule_Ignored()
    {
        var rules = new[] { Rule("amount", RuleTypes.ElementRequired, matchHint: "金额") };

        Assert.Empty(_checker.Check(Context(Chunk("金额为100元。")), rules, Array.Empty<ElementDraft>()));
    }

    [Fact]
    public void Check_ForbiddenRuleWithBlankKeyword_NoFinding()
    {
        var rules = new[] { new RuleRecord { Id = 1, Name = "", RuleType = RuleTypes.ClauseForbidden, MatchHint = "" } };

        Assert.Empty(_checker.Check(Context(Chunk("任意文本")), rules, Array.Empty<ElementDraft>()));
    }

    private static ReviewContext Context(params ChunkRecord[] chunks) => new("doc", "f.pdf", chunks);

    private static ChunkRecord Chunk(string content) => new()
    {
        Id = 7, DocumentId = "doc", Seq = 1, PageFrom = 1, PageTo = 2,
        Content = content, CharCount = content.Length, CreatedAt = "t"
    };

    private static RuleRecord Rule(string name, string type, string? matchHint = null)
        => new() { Id = 1, Name = name, RuleType = type, MatchHint = matchHint, Severity = FindingSeverities.Medium, Body = "body" };
}

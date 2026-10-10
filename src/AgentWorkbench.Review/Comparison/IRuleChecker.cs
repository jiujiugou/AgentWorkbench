namespace AgentWorkbench.Review.Comparison;

/// <summary>规则比对扩展点：新增检查器 = 新增实现。</summary>
public interface IRuleChecker
{
    string Name { get; }

    IEnumerable<FindingDraft> Check(
        ReviewContext context,
        IReadOnlyList<RuleRecord> rules,
        IReadOnlyList<ElementDraft> elements);
}

/// <summary>比对器注册表：聚合所有检查器。</summary>
public sealed class RuleCheckerRegistry
{
    private readonly IReadOnlyList<IRuleChecker> _checkers;

    public RuleCheckerRegistry(IEnumerable<IRuleChecker> checkers) => _checkers = checkers.ToList();

    public IReadOnlyList<FindingDraft> CheckAll(
        ReviewContext context, IReadOnlyList<RuleRecord> rules, IReadOnlyList<ElementDraft> elements)
    {
        var findings = new List<FindingDraft>();
        foreach (var checker in _checkers)
            findings.AddRange(checker.Check(context, rules, elements));
        return findings;
    }
}

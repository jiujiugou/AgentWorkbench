using System.Text.Json;
using AgentWorkbench.Review.Comparison;
using AgentWorkbench.Review.Extraction;

namespace AgentWorkbench.Workflow.Steps;

/// <summary>比对步骤：复用 P2 检查器，产出问题清单 JSON。</summary>
public sealed class CompareStepExecutor : IWorkflowStepExecutor
{
    private readonly ElementExtractorRegistry _extractors;
    private readonly RuleCheckerRegistry _checkers;
    private readonly IRuleRepository _rules;

    public CompareStepExecutor(ElementExtractorRegistry extractors, RuleCheckerRegistry checkers, IRuleRepository rules)
    {
        _extractors = extractors;
        _checkers = checkers;
        _rules = rules;
    }

    public string Type => "compare";

    public async Task<string> ExecuteAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var reviewContext = new ReviewContext(context.DocumentId, context.FileName, context.Chunks);
        var elements = await _extractors.ExtractAllAsync(reviewContext, cancellationToken);
        var rules = await _rules.ListEnabledAsync();
        var findings = _checkers.CheckAll(reviewContext, rules, elements);
        return JsonSerializer.Serialize(new { count = findings.Count, findings });
    }
}

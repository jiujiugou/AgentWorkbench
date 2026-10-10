using System.Text.Json;
using AgentWorkbench.Review.Extraction;

namespace AgentWorkbench.Workflow.Steps;

/// <summary>抽取步骤：复用 P2 抽取器，产出要素 JSON。</summary>
public sealed class ExtractStepExecutor : IWorkflowStepExecutor
{
    private readonly ElementExtractorRegistry _extractors;

    public ExtractStepExecutor(ElementExtractorRegistry extractors) => _extractors = extractors;

    public string Type => "extract";

    public async Task<string> ExecuteAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var reviewContext = new ReviewContext(context.DocumentId, context.FileName, context.Chunks);
        var elements = await _extractors.ExtractAllAsync(reviewContext, cancellationToken);
        return JsonSerializer.Serialize(new { count = elements.Count, elements });
    }
}

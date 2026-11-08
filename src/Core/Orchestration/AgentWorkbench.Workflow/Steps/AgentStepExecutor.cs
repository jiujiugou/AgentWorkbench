using System.Text;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentWorkbench.Workflow.Steps;

/// <summary>
/// Agent 步骤：先按文档上下文检索依据片段，再托管 Agent Harness（MAF）生成带引用的结论。
/// 前置步骤的输出会一并作为输入，因此"抽取 → 分析"这类编排无需改本类。
/// </summary>
public sealed class AgentStepExecutor : IWorkflowStepExecutor
{
    private const int MaxChars = 6000;
    private const int TopK = 8;
    private const int QueryMaxChars = 1200;
    private const int PriorStepsMaxChars = 600;

    private readonly IAgentHarness _harness;
    private readonly IKnowledgeEngine _knowledge;
    private readonly ILogger<AgentStepExecutor> _logger;

    public AgentStepExecutor(
        IAgentHarness harness,
        IKnowledgeEngine knowledge,
        ILogger<AgentStepExecutor>? logger = null)
    {
        _harness = harness;
        _knowledge = knowledge;
        _logger = logger ?? NullLogger<AgentStepExecutor>.Instance;
    }

    public string Type => "agent";

    public async Task<string> ExecuteAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var prior = CollectPriorOutputs(context);
        var query = BuildRetrievalQuery(context.FullText, prior);
        var hits = await RetrieveAsync(query, cancellationToken);

        _logger.LogInformation(
            "Workflow agent step retrieval: Provider={Provider} Hits={HitCount}",
            _knowledge.Provider, hits.Count);

        var input = BuildInput(context.PromptContent, context.FullText, prior, hits);
        var result = await _harness.RunAsync(
            new HarnessRunRequest(null, input), cancellationToken);
        return result.Text;
    }

    /// <summary>
    /// 检索降级策略：检索失败不算步骤失败。
    /// 结论里会明确带上「（无命中）」，由提示词约束模型标注依据不足，
    /// 而不是让整个工作流因为知识库不可用而红掉。
    /// </summary>
    private async Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, CancellationToken cancellationToken)
    {
        if (!_knowledge.Capabilities.Retrieve)
        {
            _logger.LogWarning(
                "知识引擎 {Provider} 不支持检索，agent 步骤降级为无依据结论。", _knowledge.Provider);
            return [];
        }

        try
        {
            return await _knowledge.RetrieveAsync(query, TopK, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检索失败，agent 步骤降级为无依据结论。Query={Query}", query);
            return [];
        }
    }

    private static string CollectPriorOutputs(WorkflowStepContext context)
    {
        var builder = new StringBuilder();
        foreach (var step in context.Steps
            .Where(s => s.OrderIndex < context.Step.OrderIndex)
            .Where(s => !string.IsNullOrWhiteSpace(s.Output))
            .OrderBy(s => s.OrderIndex))
        {
            builder.AppendLine($"## {step.StepKey}");
            builder.AppendLine(Trim(step.Output!));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string BuildRetrievalQuery(string fullText, string prior)
    {
        var builder = new StringBuilder();
        builder.AppendLine(fullText.Length <= QueryMaxChars ? fullText : fullText[..QueryMaxChars]);
        if (!string.IsNullOrWhiteSpace(prior))
            builder.AppendLine(prior.Length <= PriorStepsMaxChars ? prior : prior[..PriorStepsMaxChars]);
        return builder.ToString();
    }

    private static string BuildInput(
        string? promptContent, string fullText, string prior, IReadOnlyList<RetrievalHit> hits)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(promptContent))
            builder.AppendLine(promptContent).AppendLine();

        builder.AppendLine("【文档上下文】");
        builder.AppendLine(Trim(fullText));
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(prior))
        {
            builder.AppendLine("【前置步骤输出】");
            builder.AppendLine(Trim(prior));
            builder.AppendLine();
        }

        builder.AppendLine("【检索到的依据】");
        if (hits.Count == 0)
        {
            builder.AppendLine("（无命中；结论中涉及外部依据的部分必须标注「依据不足，需人工确认」）");
        }
        else
        {
            for (var i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                var loc = h.PageFrom > 0 ? $"{h.FileName} p.{h.PageFrom}" : h.FileName;
                builder.AppendLine(
                    $"[{i + 1}] {loc}{(string.IsNullOrWhiteSpace(h.Heading) ? "" : " · " + h.Heading)}（RRF={h.FusedScore:F3}）");
                builder.AppendLine(Trim(h.Content));
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }

    private static string Trim(string s)
        => s.Length <= MaxChars ? s : s[..MaxChars];
}

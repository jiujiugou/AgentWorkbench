using System.Text;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentWorkbench.Workflow.Steps;

/// <summary>
/// Agent 步骤：先检索设备手册与历史处置依据，再托管 Agent Harness（MAF）生成报警处置结论。
/// 原实现显式传 UseKnowledge: false，Agent 实际上从未查过知识库，结论只能是空口推断。
/// </summary>
public sealed class AgentStepExecutor : IWorkflowStepExecutor
{
    private const int MaxChars = 6000;
    private const int TopK = 8;
    private const int QueryMaxChars = 1200;
    private const int FindingsMaxChars = 600;

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
        var findings = context.FindOutput("compare") ?? "（无清单）";
        var query = BuildRetrievalQuery(context.FullText, findings);
        var hits = await RetrieveAsync(query, cancellationToken);

        _logger.LogInformation(
            "Workflow agent step retrieval: Provider={Provider} Hits={HitCount}",
            _knowledge.Provider, hits.Count);

        var input = BuildInput(context.PromptContent, context.FullText, findings, hits);
        var result = await _harness.RunAsync(
            new HarnessRunRequest(null, input, UseKnowledge: true), cancellationToken);
        return result.Text;
    }

    /// <summary>
    /// 检索降级策略：检索失败不算步骤失败。
    /// 结论里会明确带上「（无命中）」，由提示词约束模型标注「手册未覆盖，需现场确认」，
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

    private static string BuildRetrievalQuery(string fullText, string findings)
    {
        var builder = new StringBuilder();
        builder.AppendLine(fullText.Length <= QueryMaxChars ? fullText : fullText[..QueryMaxChars]);
        if (!string.IsNullOrWhiteSpace(findings))
            builder.AppendLine(findings.Length <= FindingsMaxChars ? findings : findings[..FindingsMaxChars]);
        return builder.ToString();
    }

    private static string BuildInput(
        string? promptContent, string fullText, string findings, IReadOnlyList<RetrievalHit> hits)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(promptContent))
            builder.AppendLine(promptContent).AppendLine();

        builder.AppendLine("【报警上下文】");
        builder.AppendLine(Trim(fullText));
        builder.AppendLine();

        builder.AppendLine("【规则比对与要素清单】");
        builder.AppendLine(Trim(findings));
        builder.AppendLine();

        builder.AppendLine("【检索到的手册与历史处置依据】");
        if (hits.Count == 0)
        {
            builder.AppendLine("（无命中；结论中涉及手册参数的部分必须标注「手册未覆盖，需现场确认」）");
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

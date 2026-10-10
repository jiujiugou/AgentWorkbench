using System.Text;

namespace AgentWorkbench.Workflow.Steps;

/// <summary>报告步骤：汇总前序步骤产物为 Markdown 报告。</summary>
public sealed class ReportStepExecutor : IWorkflowStepExecutor
{
    public string Type => "report";

    public Task<string> ExecuteAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# 审查报告（工作流 {context.Run.WorkflowKey}）");
        builder.AppendLine();
        builder.AppendLine($"- 文档：{context.FileName}");
        builder.AppendLine($"- 运行：#{context.Run.Id}");
        if (!string.IsNullOrWhiteSpace(context.Run.PromptVersion))
            builder.AppendLine($"- 提示词版本：{context.Run.PromptVersion}");
        builder.AppendLine();

        foreach (var step in context.Steps.Where(s => s.StepKey != context.Step.StepKey).OrderBy(s => s.OrderIndex))
        {
            builder.AppendLine($"## {step.StepKey}（{step.Status}）");
            builder.AppendLine(string.IsNullOrWhiteSpace(step.Output) ? "（无输出）" : Truncate(step.Output, 500));
            builder.AppendLine();
        }

        return Task.FromResult(builder.ToString());
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

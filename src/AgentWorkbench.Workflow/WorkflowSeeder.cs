using System.Text.Json;

namespace AgentWorkbench.Workflow;

/// <summary>工作流种子：默认定义（文档分析：agent → report）+ 默认提示词版本。</summary>
public static class WorkflowSeeder
{
    /// <summary>agent 步骤使用的提示词 key（不变，切换场景只换版本）。</summary>
    public const string AnswerPromptKey = "workflow.answer";

    /// <summary>默认工作流显示名。</summary>
    public const string DefaultWorkflowName = "文档分析工作流";

    /// <summary>默认提示词版本。</summary>
    public const string DefaultPromptVersion = "base-0.1";

    /// <summary>
    /// 默认提示词。「依据不足就直说」是硬约束：编造依据的代价远高于拒答。
    /// </summary>
    public const string DefaultPromptContent =
        "你是文档分析助手。只依据给定的【文档上下文】与【检索到的依据】作答，"
      + "输出结构化的结论与建议。引用依据时用 [编号] 标注，编号必须与【检索到的依据】中的编号一致，"
      + "不得引用不存在的编号，不得编造依据中不存在的参数、条款或事实。"
      + "依据不足以支撑结论时，明确标注「依据不足，需人工确认」并列出仍需确认的项，"
      + "不得用通用经验冒充依据。";

    public static async Task SeedWorkflowAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
        var now = DateTimeOffset.UtcNow.ToString("o");

        // 定义：不存在则建；已存在但仍是旧名（历史垂直场景）则原地升级，不依赖删库重播种子。
        var existing = await repository.GetDefinitionAsync(WorkflowService.DefaultWorkflowKey);
        if (existing is null)
        {
            await repository.UpsertDefinitionAsync(new WorkflowDefinitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = WorkflowService.DefaultWorkflowKey,
                Name = DefaultWorkflowName,
                Definition = JsonSerializer.Serialize(BuildDefinition()),
                Enabled = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else if (!string.Equals(existing.Name, DefaultWorkflowName, StringComparison.Ordinal))
        {
            existing.Name = DefaultWorkflowName;
            existing.Definition = JsonSerializer.Serialize(BuildDefinition());
            existing.UpdatedAt = now;
            await repository.UpsertDefinitionAsync(existing);
        }

        // 提示词：按版本号幂等升级（原实现是 Count == 0 才插入，改代码重启后旧库永远不生效）。
        var prompt = await repository.GetPromptAsync(AnswerPromptKey, DefaultPromptVersion);
        if (prompt is null)
        {
            await repository.AddPromptAsync(
                AnswerPromptKey, DefaultPromptVersion, DefaultPromptContent, active: true);
        }
        else if (prompt.Active != 1)
        {
            await repository.ActivatePromptAsync(AnswerPromptKey, DefaultPromptVersion);
        }
    }

    /// <summary>默认步骤链：检索增强结论 → 汇总报告。</summary>
    private static WorkflowDefinitionDocument BuildDefinition() => new()
    {
        Steps =
        [
            new WorkflowStepDefinition("answer", "agent", 0, 120, 2, AnswerPromptKey),
            new WorkflowStepDefinition("report", "report", 1, 30, 1, null)
        ]
    };
}

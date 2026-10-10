using System.Text.Json;

namespace AgentWorkbench.Workflow;

/// <summary>工作流种子：默认定义（工业设备报警处置）+ 默认提示词版本。</summary>
public static class WorkflowSeeder
{
    /// <summary>结论步骤使用的提示词 key（不变，切换场景只换版本）。</summary>
    private const string ConclusionPromptKey = "advisor.conclusion";

    /// <summary>默认工作流显示名（垂直场景：工业设备报警处置）。</summary>
    public const string DefaultWorkflowName = "报警处置工作流";

    /// <summary>工业报警处置提示词版本。</summary>
    public const string IndustrialPromptVersion = "advisor-industrial-0.1";

    /// <summary>
    /// 工业报警处置提示词。
    /// 「手册未覆盖就直说」是硬约束：工业场景臆造地址/量程/报警码的代价远高于拒答。
    /// </summary>
    public const string IndustrialPromptContent =
        "你是工业设备报警处置助手。只依据给定的报警上下文、设备手册片段与历史处置记录，"
      + "输出结构化的可能原因与排查步骤。引用依据时用 [编号] 标注，"
      + "编号必须与「检索到的手册与历史处置依据」小节中的编号一致，不得引用不存在的编号。"
      + "不得臆造手册中不存在的参数、寄存器地址、量程或报警码；手册未覆盖时明确标注「手册未覆盖，需现场确认」，"
      + "不得用通用经验冒充手册依据。若依据不足以定位根因，输出「依据不足」并列出仍需现场确认的项，禁止编造根因。";

    public static async Task SeedWorkflowAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
        var now = DateTimeOffset.UtcNow.ToString("o");

        // 定义：不存在则建；已存在但仍是旧名（合同审查）则原地升级，不依赖删库重播种。
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

        // 提示词：工业版必须存在且处于激活态。
        // 原实现是 Count == 0 才插入，导致改代码重启后旧库永远不生效；这里按版本号幂等升级。
        var industrial = await repository.GetPromptAsync(ConclusionPromptKey, IndustrialPromptVersion);
        if (industrial is null)
        {
            await repository.AddPromptAsync(
                ConclusionPromptKey, IndustrialPromptVersion, IndustrialPromptContent, active: true);
        }
        else if (industrial.Active != 1)
        {
            await repository.ActivatePromptAsync(ConclusionPromptKey, IndustrialPromptVersion);
        }
    }

    /// <summary>报警处置步骤链：要素抽取 → 规则比对 → 检索增强结论 → 报告。</summary>
    private static WorkflowDefinitionDocument BuildDefinition() => new()
    {
        Steps =
        [
            new WorkflowStepDefinition("extract", "extract", 0, 60, 1, null),
            new WorkflowStepDefinition("compare", "compare", 1, 60, 1, null),
            new WorkflowStepDefinition("agent", "agent", 2, 120, 2, ConclusionPromptKey),
            new WorkflowStepDefinition("report", "report", 3, 30, 1, null)
        ]
    };
}

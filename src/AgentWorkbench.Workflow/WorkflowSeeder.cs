using System.Text.Json;

namespace AgentWorkbench.Workflow;

/// <summary>
/// 工作流种子：默认定义（文档分析：agent → report）+ 默认提示词版本。
/// </summary>
/// <remarks>
/// 阶段 4：**提示词提为一等公民** —— 内容不再硬编码在这里，改由 <see cref="WorkflowOptions"/>
/// （配置节 <c>Workflow</c>）提供。指令层与执行层分离：改提示词改配置即可，不必改代码重编译；
/// 运行期换提示词仍走「新增版本 + 激活」（前端面板 / <c>IPromptStore</c>）。
/// </remarks>
public static class WorkflowSeeder
{
    public static async Task SeedWorkflowAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<WorkflowOptions>>().Value;
        var now = DateTimeOffset.UtcNow.ToString("o");

        // 定义：不存在则建；已存在但仍是旧名（历史垂直场景）则原地升级，不依赖删库重播种子。
        var existing = await repository.GetDefinitionAsync(options.DefaultWorkflowKey);
        if (existing is null)
        {
            await repository.UpsertDefinitionAsync(new WorkflowDefinitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = options.DefaultWorkflowKey,
                Name = options.DefaultWorkflowName,
                Definition = JsonSerializer.Serialize(BuildDefinition(options)),
                Enabled = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else if (!string.Equals(existing.Name, options.DefaultWorkflowName, StringComparison.Ordinal))
        {
            existing.Name = options.DefaultWorkflowName;
            existing.Definition = JsonSerializer.Serialize(BuildDefinition(options));
            existing.UpdatedAt = now;
            await repository.UpsertDefinitionAsync(existing);
        }

        // 提示词内容留空 = 不播种（由运营在前端自行新增版本）。
        if (string.IsNullOrWhiteSpace(options.DefaultPromptContent))
            return;

        // 提示词：按版本号幂等升级（原实现是 Count == 0 才插入，改代码重启后旧库永远不生效）。
        var prompt = await repository.GetPromptAsync(options.AnswerPromptKey, options.DefaultPromptVersion);
        if (prompt is null)
        {
            await repository.AddPromptAsync(
                options.AnswerPromptKey, options.DefaultPromptVersion, options.DefaultPromptContent, active: true);
        }
        else if (prompt.Active != 1)
        {
            await repository.ActivatePromptAsync(options.AnswerPromptKey, options.DefaultPromptVersion);
        }
    }

    /// <summary>默认步骤链：检索增强结论 → 汇总报告。步骤用的提示词 key 也来自配置。</summary>
    private static WorkflowDefinitionDocument BuildDefinition(WorkflowOptions options) => new()
    {
        Steps =
        [
            new WorkflowStepDefinition("answer", "agent", 0, 120, 2, options.AnswerPromptKey),
            new WorkflowStepDefinition("report", "report", 1, 30, 1, null)
        ]
    };
}

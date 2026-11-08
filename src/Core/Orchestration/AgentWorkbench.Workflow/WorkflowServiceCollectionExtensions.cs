using System.Text.Json;
using AgentWorkbench.Workflow.Execution;
using AgentWorkbench.Workflow.Prompting;
using AgentWorkbench.Workflow.Steps;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace AgentWorkbench.Workflow;

/// <summary>Workflow 模块（P4 Workflow Loop，MAF 图编排）：步骤编排 + 单步重跑 + 提示词版本 + 幂等登记 + MAF 检查点。</summary>
public static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddNitroWorkflow(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 提示词是配置项（配置节 Workflow），不是代码常量。
        services.AddOptions<WorkflowOptions>().Bind(configuration.GetSection(WorkflowOptions.SectionName));

        services.AddSingleton<IWorkflowStepExecutor, AgentStepExecutor>();
        services.AddSingleton<IWorkflowStepExecutor, ReportStepExecutor>();
        services.AddSingleton<WorkflowStepRegistry>();

        services.AddSingleton<IPromptStore, PromptStore>();
        services.AddSingleton<IWorkflowProgressNotifier, NullWorkflowProgressNotifier>();
        services.AddSingleton<IWorkflowRunQueue, WorkflowRunQueue>();
        services.AddSingleton<WorkflowGraphBuilder>();

        // MAF checkpoint：机制层一律用框架。FileSystemJsonCheckpointStore 是进程独占的，
        // 必须单例（现有 WorkflowWorker 单读者队列刚好串行，满足它「非线程安全」的约束）。
        services.AddSingleton<FileSystemJsonCheckpointStore>(sp =>
        {
            var paths = sp.GetRequiredService<StoragePaths>();
            var configured = configuration[$"{WorkflowOptions.SectionName}:CheckpointDirectory"];
            var directory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(paths.DataDir, "checkpoints")
                : Path.GetFullPath(configured!);
            Directory.CreateDirectory(directory);
            return new FileSystemJsonCheckpointStore(new DirectoryInfo(directory));
        });
        services.AddSingleton(sp => CheckpointManager.CreateJson(
            sp.GetRequiredService<FileSystemJsonCheckpointStore>(),
            CreateCheckpointSerializerOptions()));

        services.AddSingleton<IWorkflowService, WorkflowService>();
        services.AddHostedService<WorkflowWorker>();
        return services;
    }

    /// <summary>
    /// <c>CreateJson</c> 的文档要求「用了自定义消息/状态类型时必须传 options」。
    /// <c>WorkflowStepMessage</c> 含 WorkflowRunRecord / ChunkRecord / WorkflowStepRecord，
    /// 默认反射 resolver 已能处理；但参数不能为 null，否则恢复反序列化会炸。
    /// </summary>
    internal static JsonSerializerOptions CreateCheckpointSerializerOptions()
        => new() { PropertyNameCaseInsensitive = true };
}

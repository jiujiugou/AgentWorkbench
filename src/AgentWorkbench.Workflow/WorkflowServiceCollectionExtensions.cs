using AgentWorkbench.Workflow.Execution;
using AgentWorkbench.Workflow.Prompting;
using AgentWorkbench.Workflow.Steps;

namespace AgentWorkbench.Workflow;

/// <summary>Workflow 模块（P4 Workflow Loop，MAF 图编排）：步骤编排 + 单步重跑 + 提示词版本 + 重试/取消/超时/检查点。</summary>
public static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddNitroWorkflow(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IWorkflowStepExecutor, ExtractStepExecutor>();
        services.AddSingleton<IWorkflowStepExecutor, CompareStepExecutor>();
        services.AddSingleton<IWorkflowStepExecutor, AgentStepExecutor>();
        services.AddSingleton<IWorkflowStepExecutor, ReportStepExecutor>();
        services.AddSingleton<WorkflowStepRegistry>();

        services.AddSingleton<IPromptStore, PromptStore>();
        services.AddSingleton<IWorkflowProgressNotifier, NullWorkflowProgressNotifier>();
        services.AddSingleton<IWorkflowRunQueue, WorkflowRunQueue>();
        services.AddSingleton<WorkflowGraphBuilder>();
        services.AddSingleton<IWorkflowService, WorkflowService>();
        services.AddHostedService<WorkflowWorker>();
        return services;
    }
}

using AgentWorkbench.Workflow.Prompting;
using AgentWorkbench.Workflow.Steps;
using Microsoft.Agents.AI.Workflows;

namespace AgentWorkbench.Workflow.Execution;

/// <summary>
/// 将工作流定义编译为 MAF（Microsoft Agent Framework）<see cref="Microsoft.Agents.AI.Workflows.Workflow"/> 图：
/// 步骤按 order 顺序逐节点相连，末节点作为工作流输出源。
/// 编排、路由、事件与检查点全部交由 MAF，底座只保留步骤策略与持久化。
/// </summary>
public sealed class WorkflowGraphBuilder(
    WorkflowStepRegistry registry,
    IWorkflowRepository repository,
    IPromptStore prompts,
    IWorkflowProgressNotifier notifier,
    ILoggerFactory loggerFactory)
{
    public Microsoft.Agents.AI.Workflows.Workflow Build(WorkflowDefinition definition)
    {
        var ordered = definition.Steps.OrderBy(s => s.Order).ToList();
        if (ordered.Count == 0)
            throw new InvalidOperationException($"工作流定义 {definition.Key} 未定义任何步骤");

        var nodes = ordered
            .Select(step => (Executor)new WorkflowStepExecutorNode(
                step,
                registry.Get(step.Type),
                repository,
                prompts,
                notifier,
                loggerFactory.CreateLogger<WorkflowStepExecutorNode>()))
            .ToList();

        var builder = new WorkflowBuilder(nodes[0]);
        for (var i = 0; i < nodes.Count - 1; i++)
            builder.AddEdge(nodes[i], nodes[i + 1]);
        builder.WithOutputFrom(nodes[^1]);
        return builder.Build();
    }
}

namespace AgentWorkbench.Workflow.Execution;

/// <summary>
/// 工作流运行消息：在 MAF（Microsoft Agent Framework）图执行器之间传递运行上下文与已加载的步骤记录。
/// 步骤产物写入 <see cref="WorkflowStepRecord.Output"/> 后随消息向下游传递，供报告步骤汇总。
/// </summary>
public sealed class WorkflowStepMessage
{
    public required WorkflowRunRecord Run { get; init; }

    public required string DocumentId { get; init; }

    public required string FileName { get; init; }

    public required IReadOnlyList<ChunkRecord> Chunks { get; init; }

    public required List<WorkflowStepRecord> Steps { get; init; }
}

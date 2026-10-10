namespace AgentWorkbench.Workflow;

/// <summary>工作流进度通知扩展点（默认空实现；Webapi 用 SignalR 覆盖）。</summary>
public interface IWorkflowProgressNotifier
{
    Task NotifyAsync(long runId, string stepKey, string status, int attempts, string? output, CancellationToken cancellationToken);
}

/// <summary>默认空实现。</summary>
public sealed class NullWorkflowProgressNotifier : IWorkflowProgressNotifier
{
    public Task NotifyAsync(long runId, string stepKey, string status, int attempts, string? output, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

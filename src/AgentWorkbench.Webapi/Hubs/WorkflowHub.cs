using AgentWorkbench.Workflow;
using Microsoft.AspNetCore.SignalR;

namespace AgentWorkbench.Webapi.Hubs;

/// <summary>工作流进度实时通道：按 runId 分组推送。</summary>
public sealed class WorkflowHub : Hub
{
    public Task JoinRun(long runId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(runId));

    public Task LeaveRun(long runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(runId));

    internal static string Group(long runId) => $"workflow-{runId}";
}

/// <summary>用 SignalR 实现工作流进度通知（覆盖 Workflow 模块的空实现）。</summary>
public sealed class SignalRWorkflowProgressNotifier : IWorkflowProgressNotifier
{
    private readonly IHubContext<WorkflowHub> _hub;

    public SignalRWorkflowProgressNotifier(IHubContext<WorkflowHub> hub) => _hub = hub;

    public Task NotifyAsync(long runId, string stepKey, string status, int attempts, string? output, CancellationToken cancellationToken)
        => _hub.Clients
            .Group(WorkflowHub.Group(runId))
            .SendAsync("ReceiveWorkflowProgress", new { runId, stepKey, status, attempts, output }, cancellationToken);
}

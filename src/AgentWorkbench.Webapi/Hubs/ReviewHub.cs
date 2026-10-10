using AgentWorkbench.Review;
using Microsoft.AspNetCore.SignalR;

namespace AgentWorkbench.Webapi.Hubs;

/// <summary>审查进度实时通道：按 runId 分组推送。</summary>
public sealed class ReviewHub : Hub
{
    public Task JoinRun(long runId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(runId));

    public Task LeaveRun(long runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(runId));

    internal static string Group(long runId) => $"review-{runId}";
}

/// <summary>用 SignalR 实现审查进度通知（覆盖 Review 模块的空实现）。</summary>
public sealed class SignalRReviewProgressNotifier : IReviewProgressNotifier
{
    private readonly IHubContext<ReviewHub> _hub;

    public SignalRReviewProgressNotifier(IHubContext<ReviewHub> hub) => _hub = hub;

    public Task NotifyAsync(long runId, string status, string stepsJson, CancellationToken cancellationToken)
        => _hub.Clients
            .Group(ReviewHub.Group(runId))
            .SendAsync("ReceiveReviewProgress", new { runId, status, steps = stepsJson }, cancellationToken);
}

namespace AgentWorkbench.Review;

/// <summary>审查进度通知扩展点（默认空实现；Webapi 用 SignalR 覆盖）。</summary>
public interface IReviewProgressNotifier
{
    Task NotifyAsync(long runId, string status, string stepsJson, CancellationToken cancellationToken);
}

/// <summary>默认空实现（无实时通道时静默，前端可轮询）。</summary>
public sealed class NullReviewProgressNotifier : IReviewProgressNotifier
{
    public Task NotifyAsync(long runId, string status, string stepsJson, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

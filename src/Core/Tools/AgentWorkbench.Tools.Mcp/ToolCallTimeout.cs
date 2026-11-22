namespace AgentWorkbench.Tools.Mcp;

/// <summary>
/// 给一次外部调用加超时，并严格区分「外部取消」与「内部超时」。
/// </summary>
/// <remarks>
/// 为什么值得单独一个方法：超时算「不知道成没成」（要查证、不重放），外部取消算「调用者主动停」，
/// 两者在 .NET 里都是 <see cref="OperationCanceledException"/>，混在一起会把取消误判成传输故障。
/// 真正的分界只有一个：<c>ct.IsCancellationRequested</c> 是不是外部令牌触发的。
/// </remarks>
public static class ToolCallTimeout
{
    /// <summary>
    /// 在 <paramref name="timeout"/> 内执行 <paramref name="work"/>。
    /// 超时抛 <see cref="TimeoutException"/>；外部取消原样抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> work,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await work(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 外部取消：调用者主动停，不是「不知道成没成」
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"外部调用超时（{timeout.TotalSeconds:0.###}s）");
        }
    }
}

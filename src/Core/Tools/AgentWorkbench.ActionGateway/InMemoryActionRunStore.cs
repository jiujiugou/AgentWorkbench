namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// run 状态、幂等去重表与终态缓存的内存实现（底座侧，M1 设计 §6）。
/// Checkpoint 只存执行进度字节，不存业务单据状态；去重按 request_id。
/// </summary>
public sealed class InMemoryActionRunStore : IActionRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionResult> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claims = new(StringComparer.Ordinal);

    public int ClaimAttempts { get; private set; }
    public int SuspendCount { get; private set; }
    public int ResumeCount { get; private set; }

    public ValueTask<RunCheckpoint?> LoadCheckpointAsync(string requestId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(_checkpoints.TryGetValue(requestId, out var cp) ? cp : null);
    }

    public ValueTask<RunCheckpoint> SuspendAsync(string requestId, string state, string progressBlob, CancellationToken ct)
    {
        lock (_gate)
        {
            SuspendCount++;
            var cp = new RunCheckpoint(requestId, state, progressBlob, DateTimeOffset.UtcNow);
            _checkpoints[requestId] = cp;
            return ValueTask.FromResult(cp);
        }
    }

    public ValueTask<RunCheckpoint?> ResumeAsync(string requestId, CancellationToken ct)
    {
        lock (_gate)
        {
            ResumeCount++;
            return ValueTask.FromResult(_checkpoints.TryGetValue(requestId, out var cp) ? cp : null);
        }
    }

    public ValueTask<bool> TryClaimIdempotencyAsync(string requestId, string? idempotencyKey, CancellationToken ct)
    {
        lock (_gate)
        {
            ClaimAttempts++;
            return ValueTask.FromResult(_claims.Add(requestId));
        }
    }

    public ValueTask<ActionResult?> LoadResultAsync(string requestId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(_results.TryGetValue(requestId, out var r) ? r : null);
    }

    public ValueTask SaveResultAsync(ActionResult result, CancellationToken ct)
    {
        lock (_gate) _results[result.RequestId] = result;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 只清终态缓存、保留幂等领取表。用于验证「进程重启后终态丢了、但去重表还在」时，
    /// 第二次写动作仍会被幂等领取挡住 —— 这条路径不会被 LoadResultAsync 的终态回放掩盖。
    /// </summary>
    public void ClearResults()
    {
        lock (_gate) _results.Clear();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _checkpoints.Clear();
            _results.Clear();
            _claims.Clear();
            ClaimAttempts = 0;
            SuspendCount = 0;
            ResumeCount = 0;
        }
    }
}

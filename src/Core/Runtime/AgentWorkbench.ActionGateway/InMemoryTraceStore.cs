namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// 审计落库通道的内存实现：只提供结构与落库通道，不产生业务内容（M1 设计 §6）。
/// 同时实现 ITraceSink 与 IAuditQuery，支持按 request_id 反查完整轨迹。
/// Available=false 用于验证「轨迹写不进去必须拒绝」这道 fail-closed 门禁。
/// </summary>
public sealed class InMemoryTraceStore : ITraceSink, IAuditQuery, IActionHistoryQuery
{
    private readonly object _gate = new();
    private readonly List<TraceEvent> _events = [];

    public bool Available { get; set; } = true;
    public int WriteAttempts { get; private set; }

    public IReadOnlyList<TraceEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public ValueTask<AuditWriteResult> WriteAsync(TraceEvent trace, CancellationToken ct)
    {
        lock (_gate)
        {
            WriteAttempts++;
            if (!Available)
                return ValueTask.FromResult(new AuditWriteResult(false, "trace sink unavailable; fail-closed"));
            _events.Add(trace);
        }
        return ValueTask.FromResult(new AuditWriteResult(true, "written"));
    }

    public ValueTask<IReadOnlyList<TraceEvent>> AuditTrailAsync(string requestId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<TraceEvent> result = _events
                .Where(e => string.Equals(e.RequestId, requestId, StringComparison.Ordinal))
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// 列表视图。内存实现<b>不持有结果表</b>，因此 <c>Status</c> / <c>Code</c> 一律为 null
    /// （只表示「尚未收口」，不代表待审批）——终态以 SQLite 实现为准。
    /// <c>ExternalId</c> 可从轨迹里取到（执行那一格写过），用于本地演练时仍能看到外部单号。
    /// </summary>
    public ValueTask<IReadOnlyList<ActionSummary>> RecentAsync(int limit, CancellationToken ct)
    {
        var take = limit is > 0 and <= 500 ? limit : 50;
        lock (_gate)
        {
            IReadOnlyList<ActionSummary> rows = _events
                .GroupBy(e => e.RequestId, StringComparer.Ordinal)
                .Select(g => new ActionSummary(
                    g.Key,
                    g.OrderBy(e => e.At).First().Intent,
                    g.Min(e => e.At),
                    Status: null,
                    Code: null,
                    g.Select(e => e.ExternalId).LastOrDefault(id => !string.IsNullOrEmpty(id))))
                .OrderByDescending(s => s.At)
                .Take(take)
                .ToArray();
            return ValueTask.FromResult(rows);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _events.Clear();
            WriteAttempts = 0;
            Available = true;
        }
    }
}

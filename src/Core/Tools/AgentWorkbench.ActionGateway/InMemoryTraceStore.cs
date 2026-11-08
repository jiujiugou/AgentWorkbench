namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// 审计落库通道的内存实现：只提供结构与落库通道，不产生业务内容（M1 设计 §6）。
/// 同时实现 ITraceSink 与 IAuditQuery，支持按 request_id 反查完整轨迹。
/// Available=false 用于验证「轨迹写不进去必须拒绝」这道 fail-closed 门禁。
/// </summary>
public sealed class InMemoryTraceStore : ITraceSink, IAuditQuery
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

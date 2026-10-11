using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace AgentWorkbench.Memory;

/// <summary>会话存储：按 sessionId 取回或新建一个 Agent 会话。</summary>
public interface ISessionStore
{
    Task<AgentSession> GetOrCreateAsync(
        string sessionId,
        Func<CancellationToken, Task<AgentSession>> factory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 进程内会话存储。重启即丢，多实例不共享 —— 持久化版本见底座建设清单 P1-3。
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    public async Task<AgentSession> GetOrCreateAsync(
        string sessionId,
        Func<CancellationToken, Task<AgentSession>> factory,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
            return existing;

        var session = await factory(cancellationToken);
        return _sessions.GetOrAdd(sessionId, session);
    }
}

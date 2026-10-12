using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentWorkbench.Memory;

/// <summary>
/// 会话存储：按 <c>sessionId</c> 取回或新建一个 Agent 会话，并负责把它写回。
/// </summary>
/// <remarks>
/// 契约只认 <see cref="AgentSession"/> 与 <see cref="ISessionFactory"/>：
/// 「会话对象怎么来、怎么序列化」由编排层（AgentHarness）实现，
/// 「存哪儿」由本模块的内存版或 <c>Memory.Persistence</c> 的落库版决定。
/// </remarks>
public interface ISessionStore
{
    /// <summary>命中就返回已有会话（落库版会反序列化），未命中就用 <paramref name="factory"/> 建一个新的。</summary>
    Task<AgentSession> GetOrCreateAsync(
        string sessionId,
        ISessionFactory factory,
        CancellationToken cancellationToken = default);

    /// <summary>把一次 run 之后的会话状态写回。内存版无副作用，落库版序列化后 UPSERT。</summary>
    Task SaveAsync(
        string sessionId,
        ISessionFactory factory,
        AgentSession session,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话材料化：建新会话 / 序列化 / 反序列化。由 AgentHarness 用当次 run 的 agent 实现，
/// 这样存储实现不需要认识具体的模型客户端。
/// </summary>
public interface ISessionFactory
{
    ValueTask<AgentSession> CreateAsync(CancellationToken cancellationToken);

    ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken);

    ValueTask<AgentSession> DeserializeAsync(JsonElement state, CancellationToken cancellationToken);
}

/// <summary>
/// 进程内会话存储：重启即丢。带空闲 TTL 与条数上限（LRU 淘汰），因此长跑服务内存有界。
/// 需要跨重启续存的场景由 <c>AgentWorkbench.Memory.Persistence</c> 的落库版接管。
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private sealed record Entry(AgentSession Session, DateTimeOffset LastAccess);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    /// <summary>建会话闸门：<c>factory</c> 含 await，只能用异步锁；只护住「未命中 → 新建」这一段。</summary>
    private readonly SemaphoreSlim _createGate = new(1, 1);

    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;

    public SessionStore(IOptions<MemoryOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _time = timeProvider;
    }

    public async Task<AgentSession> GetOrCreateAsync(
        string sessionId,
        ISessionFactory factory,
        CancellationToken cancellationToken = default)
    {
        // 快路径：已建好且未过期就直接返回，不碰锁。
        if (TryGetLive(sessionId, out var existing))
            return existing;

        await _createGate.WaitAsync(cancellationToken);
        try
        {
            // 双检：等锁期间可能已被别的请求建好，避免重复创建后又丢掉。
            if (TryGetLive(sessionId, out existing))
                return existing;

            var session = await factory.CreateAsync(cancellationToken);
            Store(sessionId, session);
            return session;
        }
        finally
        {
            _createGate.Release();
        }
    }

    public Task SaveAsync(
        string sessionId,
        ISessionFactory factory,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        // 对象本就在内存里；这里只刷新访问时间，让活跃会话不被 TTL 淘汰。
        Store(sessionId, session);
        return Task.CompletedTask;
    }

    private bool TryGetLive(string sessionId, out AgentSession session)
    {
        session = null!;
        if (!_sessions.TryGetValue(sessionId, out var entry))
            return false;

        var now = _time.GetUtcNow();
        if (IsExpired(entry, now))
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        _sessions[sessionId] = entry with { LastAccess = now };
        session = entry.Session;
        return true;
    }

    private bool IsExpired(Entry entry, DateTimeOffset now)
        => _options.SessionTtlMinutes > 0
           && now - entry.LastAccess >= TimeSpan.FromMinutes(_options.SessionTtlMinutes);

    private void Store(string sessionId, AgentSession session)
    {
        _sessions[sessionId] = new Entry(session, _time.GetUtcNow());
        EnforceCapacity();
    }

    /// <summary>超过上限时淘汰最久未访问的会话；并发下多淘汰一两条是可接受的近似。</summary>
    private void EnforceCapacity()
    {
        if (_options.MaxCachedSessions <= 0)
            return;

        var overflow = _sessions.Count - _options.MaxCachedSessions;
        if (overflow <= 0)
            return;

        foreach (var victim in _sessions.OrderBy(pair => pair.Value.LastAccess).Take(overflow))
            _sessions.TryRemove(victim.Key, out _);
    }
}

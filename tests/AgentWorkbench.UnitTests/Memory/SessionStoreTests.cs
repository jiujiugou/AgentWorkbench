using AgentWorkbench.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 进程内会话存储：同 id 复用是正例，换 id / 关开关必须新建是负控；
/// TTL 与上限各配一个「把旋钮关掉就不淘汰」的负控，证明淘汰真的生效。
/// </summary>
public sealed class SessionStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-12T00:00:00Z");

    private static SessionStore NewStore(MemoryOptions options, TimeProvider clock)
        => new(Options.Create(options), clock);

    [Fact]
    public async Task SameSessionId_ReturnsSameInstance()
    {
        var store = NewStore(new MemoryOptions(), new MutableTimeProvider(T0));
        var factory = new CountingSessionFactory();

        var first = await store.GetOrCreateAsync("s-1", factory);
        var second = await store.GetOrCreateAsync("s-1", factory);

        Assert.Same(first, second);
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task UnknownSessionId_CreatesNewInstance()
    {
        var store = NewStore(new MemoryOptions(), new MutableTimeProvider(T0));
        var factory = new CountingSessionFactory();

        var first = await store.GetOrCreateAsync("s-1", factory);
        var other = await store.GetOrCreateAsync("s-2", factory);

        Assert.NotSame(first, other);
        Assert.Equal(2, factory.Created);
    }

    [Fact]
    public async Task ExpiredSession_IsTreatedAsMiss()
    {
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(new MemoryOptions { SessionTtlMinutes = 10 }, clock);
        var factory = new CountingSessionFactory();

        var first = await store.GetOrCreateAsync("s-1", factory);
        clock.Now = T0.AddMinutes(11);
        var second = await store.GetOrCreateAsync("s-1", factory);

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.Created);
    }

    [Fact]
    public async Task SameCase_WithTtlDisabled_KeepsSession()
    {
        // 负控：同一场景，只把 TTL 关掉（0）—— 时间推进多久都必须还是同一个会话。
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(new MemoryOptions { SessionTtlMinutes = 0 }, clock);
        var factory = new CountingSessionFactory();

        var first = await store.GetOrCreateAsync("s-1", factory);
        clock.Now = T0.AddDays(365);
        var second = await store.GetOrCreateAsync("s-1", factory);

        Assert.Same(first, second);
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task OverCapacity_EvictsLeastRecentlyUsed()
    {
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(new MemoryOptions { MaxCachedSessions = 2 }, clock);
        var factory = new CountingSessionFactory();

        var a = await Touch(store, factory, "a", clock);
        var b = await Touch(store, factory, "b", clock);
        _ = await Touch(store, factory, "c", clock);   // 超上限，最久未用的 a 被淘汰

        Assert.Same(b, await store.GetOrCreateAsync("b", factory));   // b 还在
        Assert.NotSame(a, await store.GetOrCreateAsync("a", factory)); // a 已被淘汰 → 新建
        Assert.Equal(4, factory.Created);
    }

    [Fact]
    public async Task SameCase_WithCapacityDisabled_DoesNotEvict()
    {
        // 负控：上限设为 0（不限）—— 插再多也不该淘汰。
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(new MemoryOptions { MaxCachedSessions = 0 }, clock);
        var factory = new CountingSessionFactory();

        var a = await Touch(store, factory, "a", clock);
        _ = await Touch(store, factory, "b", clock);
        _ = await Touch(store, factory, "c", clock);

        Assert.Same(a, await store.GetOrCreateAsync("a", factory));
        Assert.Equal(3, factory.Created);
    }

    private static async Task<Microsoft.Agents.AI.AgentSession> Touch(
        SessionStore store, CountingSessionFactory factory, string sessionId, MutableTimeProvider clock)
    {
        // 每次访问都推进时钟：访问时间决定 LRU 顺序。
        clock.Now = clock.Now.AddSeconds(1);
        return await store.GetOrCreateAsync(sessionId, factory);
    }
}

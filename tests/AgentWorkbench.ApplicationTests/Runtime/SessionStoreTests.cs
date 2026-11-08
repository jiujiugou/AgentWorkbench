using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkbench.Runtime.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// F4.1–F4.4：进程内 <see cref="SessionStore"/> 的存取 / TTL / 容量 / 写回。
/// 全部用自建 FakeTimeProvider 精确控制时间，断言具体时间点、新建次数、存储条数与键集合。
/// 预期：本文件全部绿（F4.7 的预期红在 SessionContractTests.cs）。
///
/// 观察内部状态的方式：<see cref="SessionStore"/> 的缓存字典 <c>_sessions</c> 是 private，
/// 而本任务禁止改 src/ 生产代码暴露它，故 <see cref="SessionStoreProbe"/> 用反射「只读」该字段，
/// 字段名 / Entry 属性名均来自 SessionStore.cs:50、:52（已 Read 源码确认），不构造任何对象。
/// </summary>
public sealed class SessionStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static SessionStore CreateStore(FakeTimeProvider time, int ttlMinutes, int maxCached)
        => new(Options.Create(new MemoryOptions
        {
            SessionTtlMinutes = ttlMinutes,
            MaxCachedSessions = maxCached,
        }), time);

    /// <summary>每个会话都是独立实例，命中 / 新建可用 Assert.Same / Assert.NotSame 区分。</summary>
    private static AgentSession NewSession() => new Mock<AgentSession>().Object;

    // [F4.1] get-or-create 幂等：命中返回已有，未命中用 ISessionFactory 新建
    // 负控：删掉 SessionStore.cs:72 的快路径 `if (TryGetLive(sessionId, out var existing)) return existing;`
    //       （或 Store 不写回字典）→ 第二次 GetOrCreate 会再次 new，`Assert.Same` 与 `CreateCount==1` 会红。
    [Fact]
    public async Task F41_GetOrCreate_IsIdempotent_SameIdHits_DifferentIdCreates()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time, ttlMinutes: 0, maxCached: 0);
        var factory = new CountingSessionFactory();

        // 正例：同 id 连取两次 → 同一会话实例、只新建 1 次、存储里只有 1 条
        var first = await store.GetOrCreateAsync("s1", factory);
        var second = await store.GetOrCreateAsync("s1", factory);
        Assert.Same(first, second);
        Assert.Equal(1, factory.CreateCount);
        Assert.Single(factory.Created);
        Assert.Equal(1, SessionStoreProbe.Count(store));
        Assert.Equal(new[] { "s1" }, SessionStoreProbe.Ids(store));

        // 反例对照：不同 id → 新建，新建 2 次、存储 2 条，且新会话与旧会话不是同一实例
        var other = await store.GetOrCreateAsync("s2", factory);
        Assert.NotSame(first, other);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, SessionStoreProbe.Count(store));
        Assert.Equal(new[] { "s1", "s2" }, SessionStoreProbe.Ids(store));

        // 再取 s1 仍命中（不被 s2 的新建影响）
        var again = await store.GetOrCreateAsync("s1", factory);
        Assert.Same(first, again);
        Assert.Equal(2, factory.CreateCount);
    }

    // [F4.2] TTL：空闲超 SessionTtlMinutes 过期（0 = 不过期）
    // 负控：把 SessionStore.cs:121-123 的 IsExpired 改成恒 false（关掉过期判断）
    //       → 「过期」用例的 Assert.NotSame / CreateCount==2 红；把 `>=` 改成 `>` → 恰好 TTL 边界用例红；
    //       去掉 `SessionTtlMinutes > 0 &&` → 「0 = 不过期」对照用例红。
    [Fact]
    public async Task F42_Ttl_NotExpiredHits_AtTtlExpires_TtlZeroNeverExpires()
    {
        // 用例 1（未过期）：TTL=60，前进 59 分钟仍命中，且访问时间被刷新为具体值 T0+59min
        var hitTime = new FakeTimeProvider();
        var hitStore = CreateStore(hitTime, ttlMinutes: 60, maxCached: 0);
        var hitFactory = new CountingSessionFactory();
        var hitFirst = await hitStore.GetOrCreateAsync("s", hitFactory);        // LastAccess = T0
        hitTime.Now = T0.AddMinutes(59);
        var hitSecond = await hitStore.GetOrCreateAsync("s", hitFactory);
        Assert.Same(hitFirst, hitSecond);                                       // 未过期 → 命中
        Assert.Equal(1, hitFactory.CreateCount);                                // 没有新建
        Assert.Equal(T0.AddMinutes(59), SessionStoreProbe.Entry(hitStore, "s").LastAccess);
        Assert.Equal(1, SessionStoreProbe.Count(hitStore));

        // 用例 2（过期，反例对照）：恰好达到 TTL（IsExpired 用 >=）→ 新会话，旧内容不可读
        var expTime = new FakeTimeProvider();
        var expStore = CreateStore(expTime, ttlMinutes: 60, maxCached: 0);
        var expFactory = new CountingSessionFactory();
        var expFirst = await expStore.GetOrCreateAsync("s", expFactory);        // LastAccess = T0
        expTime.Now = T0.AddMinutes(60);
        var expSecond = await expStore.GetOrCreateAsync("s", expFactory);
        Assert.NotSame(expFirst, expSecond);                                    // 旧会话不可读
        Assert.Equal(2, expFactory.CreateCount);                                // 重新新建 1 次
        Assert.Equal(1, SessionStoreProbe.Count(expStore));                     // 旧行先删再建，总数仍 1
        Assert.Same(expSecond, SessionStoreProbe.Entry(expStore, "s").Session); // 存的是新会话

        // 用例 3（反例对照）：SessionTtlMinutes = 0 → 永不过期，前进 10000 天仍命中
        var neverTime = new FakeTimeProvider();
        var neverStore = CreateStore(neverTime, ttlMinutes: 0, maxCached: 0);
        var neverFactory = new CountingSessionFactory();
        var neverFirst = await neverStore.GetOrCreateAsync("s", neverFactory);
        neverTime.Now = T0.AddDays(10000);
        var neverSecond = await neverStore.GetOrCreateAsync("s", neverFactory);
        Assert.Same(neverFirst, neverSecond);
        Assert.Equal(1, neverFactory.CreateCount);
        Assert.Equal(1, SessionStoreProbe.Count(neverStore));
    }

    // [F4.3] 容量上限：进程内缓存 ≤ MaxCachedSessions（0 = 不限），超限时淘汰最久未用
    // 负控：删掉 SessionStore.cs:128 的 EnforceCapacity() 调用 → 「Count==2」「Ids=={A,C}」红；
    //       去掉 TryGetLive 里 :116 的 `entry with { LastAccess = now }`（命中不刷新）→ 淘汰 A 而非 B，
    //       「Ids=={A,C}」红（退化成 FIFO，验不出 LRU）；把 `MaxCachedSessions <= 0` 改成 `== 0`
    //       → 「0 = 不限」对照用例红。
    [Fact]
    public async Task F43_Capacity_EvictsLeastRecentlyUsed_OnlyWhenOverLimit()
    {
        // 正例：Max=2，命中刷新 LRU；第三次新建淘汰「最久未用」的 B（不是最早创建的 A）
        var time = new FakeTimeProvider();
        var store = CreateStore(time, ttlMinutes: 0, maxCached: 2);
        var factory = new CountingSessionFactory();

        var a = await store.GetOrCreateAsync("A", factory);        // A @ T0，Count=1
        time.Now = T0.AddMinutes(1);
        await store.GetOrCreateAsync("B", factory);                // B @ T0+1min，Count=2
        time.Now = T0.AddMinutes(2);
        var aAgain = await store.GetOrCreateAsync("A", factory);   // 命中 A → LastAccess 刷新为 T0+2min
        Assert.Same(a, aAgain);
        Assert.Equal(2, factory.CreateCount);                      // 命中不新建，Count 仍为 2
        time.Now = T0.AddMinutes(3);
        var c = await store.GetOrCreateAsync("C", factory);        // C @ T0+3min → Count=3 → overflow=1

        Assert.Equal(3, factory.CreateCount);
        Assert.Equal(2, SessionStoreProbe.Count(store));            // 总数不越界
        Assert.Equal(new[] { "A", "C" }, SessionStoreProbe.Ids(store)); // 淘汰 B（最久未用），保留 A/C
        Assert.Equal(T0.AddMinutes(2), SessionStoreProbe.Entry(store, "A").LastAccess);
        Assert.Equal(T0.AddMinutes(3), SessionStoreProbe.Entry(store, "C").LastAccess);
        Assert.Same(c, SessionStoreProbe.Entry(store, "C").Session);

        // 反例对照 1：未超上限（Max=3）→ 3 条全在，无淘汰
        var underTime = new FakeTimeProvider();
        var underStore = CreateStore(underTime, ttlMinutes: 0, maxCached: 3);
        var underFactory = new CountingSessionFactory();
        underTime.Now = T0;
        await underStore.GetOrCreateAsync("A", underFactory);
        underTime.Now = T0.AddMinutes(1);
        await underStore.GetOrCreateAsync("B", underFactory);
        underTime.Now = T0.AddMinutes(2);
        await underStore.GetOrCreateAsync("C", underFactory);
        Assert.Equal(3, SessionStoreProbe.Count(underStore));
        Assert.Equal(new[] { "A", "B", "C" }, SessionStoreProbe.Ids(underStore));

        // 反例对照 2：超上限 vs 未超上限的极端——Max=0（不限）→ 4 条全在
        var unlimitedTime = new FakeTimeProvider();
        var unlimitedStore = CreateStore(unlimitedTime, ttlMinutes: 0, maxCached: 0);
        var unlimitedFactory = new CountingSessionFactory();
        for (var i = 0; i < 4; i++)
        {
            unlimitedTime.Now = T0.AddMinutes(i);
            await unlimitedStore.GetOrCreateAsync($"K{i}", unlimitedFactory);
        }
        Assert.Equal(4, SessionStoreProbe.Count(unlimitedStore));
        Assert.Equal(new[] { "K0", "K1", "K2", "K3" }, SessionStoreProbe.Ids(unlimitedStore));
        Assert.Equal(4, unlimitedFactory.CreateCount);
    }

    // [F4.4] 写回：内存版无副作用（不序列化），写后能读回，最后一轮写回生效，且刷新访问时间
    // 负控：删掉 SessionStore.cs:99 的 `Store(sessionId, session)` → 「Entry.Session==round3」
    //       「LastAccess==T0+30min」与「写回后 y 仍存活」三条红；把 SaveAsync 改成调用
    //       factory.SerializeAsync（错误落库化）→ SerializeCount==0 红；把 Store 改成不覆盖旧会话
    //       → 「最后一轮写回生效」红（写回顺序倒置）。
    [Fact]
    public async Task F44_SaveAsync_NoSideEffect_LastWriteWins_AndRefreshesAccessTime()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time, ttlMinutes: 60, maxCached: 0);
        var factory = new CountingSessionFactory();
        var created = await store.GetOrCreateAsync("s", factory);   // @ T0

        // 连续 3 轮写回，每轮是本轮状态（不同实例）；逐轮断言「写回的会话即字典里的会话」
        time.Now = T0.AddMinutes(10);
        var round1 = NewSession();
        await store.SaveAsync("s", factory, round1);
        Assert.Same(round1, SessionStoreProbe.Entry(store, "s").Session);
        Assert.Equal(T0.AddMinutes(10), SessionStoreProbe.Entry(store, "s").LastAccess);

        time.Now = T0.AddMinutes(20);
        var round2 = NewSession();
        await store.SaveAsync("s", factory, round2);
        Assert.Same(round2, SessionStoreProbe.Entry(store, "s").Session);
        Assert.Equal(T0.AddMinutes(20), SessionStoreProbe.Entry(store, "s").LastAccess);

        time.Now = T0.AddMinutes(30);
        var round3 = NewSession();
        await store.SaveAsync("s", factory, round3);
        Assert.Same(round3, SessionStoreProbe.Entry(store, "s").Session);   // 顺序 round1→round2→round3，最后生效
        Assert.Equal(T0.AddMinutes(30), SessionStoreProbe.Entry(store, "s").LastAccess);

        Assert.Equal(0, factory.SerializeCount);    // 内存版无副作用：一次都不序列化
        Assert.Equal(0, factory.DeserializeCount);
        Assert.Equal(1, factory.CreateCount);       // 写回不新建

        // 写后读回（未过期）：命中且返回最后一轮写回的会话
        time.Now = T0.AddMinutes(50);
        var readBack = await store.GetOrCreateAsync("s", factory);
        Assert.Same(round3, readBack);
        Assert.NotSame(created, readBack);          // 已被第 1 轮写回覆盖，初始会话不再可读
        Assert.Equal(1, factory.CreateCount);

        // 反例对照：写回刷新访问时间 → 活跃会话不被 TTL 淘汰；未写回的会话在同样时刻过期
        var pairTime = new FakeTimeProvider();
        var pairStore = CreateStore(pairTime, ttlMinutes: 60, maxCached: 0);
        var pairFactory = new CountingSessionFactory();
        var x = await pairStore.GetOrCreateAsync("x", pairFactory);   // x @ T0（之后不写回）
        await pairStore.GetOrCreateAsync("y", pairFactory);           // y @ T0
        pairTime.Now = T0.AddMinutes(30);
        var yRound2 = NewSession();
        await pairStore.SaveAsync("y", pairFactory, yRound2);          // y 刷新为 @ T0+30min
        pairTime.Now = T0.AddMinutes(70);
        var xAfter = await pairStore.GetOrCreateAsync("x", pairFactory); // 70 >= 60 → 过期新建
        var yAfter = await pairStore.GetOrCreateAsync("y", pairFactory); // 40 < 60 → 仍命中
        Assert.NotSame(x, xAfter);
        Assert.Same(yRound2, yAfter);
        Assert.Equal(3, pairFactory.CreateCount);   // x、y 各建 1 次 + x 过期后重建 1 次
    }
}

/// <summary>
/// 自建可控时间源。测试项目未引用 Microsoft.Extensions.Time.Testing.FakeTimeProvider，
/// 且规则禁止改 csproj，故按任务指示自行覆盖 GetUtcNow。
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// ISessionFactory 测试替身：CreateAsync 每次返回一个可区分的 AgentSession 实例并计数；
/// Serialize/Deserialize 可按需抛异常，用于模拟「快照损坏 / 版本不识别」。
/// 生产代码 AgentSessionFactory.cs 是通过 AIAgent.CreateSessionAsync 创建 AgentSession（没有 new），
/// 因此这里用 Moq 代理抽象基类 AgentSession 的实例作为不透明 token，不用反射 / FormatterServices 构造。
/// </summary>
internal sealed class CountingSessionFactory : ISessionFactory
{
    public int CreateCount { get; private set; }
    public int SerializeCount { get; private set; }
    public int DeserializeCount { get; private set; }
    public bool ThrowOnSerialize { get; set; }
    public bool ThrowOnDeserialize { get; set; }
    public List<AgentSession> Created { get; } = new();

    public ValueTask<AgentSession> CreateAsync(CancellationToken cancellationToken)
    {
        var session = new Mock<AgentSession>().Object;
        CreateCount++;
        Created.Add(session);
        return ValueTask.FromResult(session);
    }

    public ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken)
    {
        SerializeCount++;
        if (ThrowOnSerialize)
            throw new InvalidOperationException("快照序列化失败（模拟损坏 / 版本不识别）");
        using var document = JsonDocument.Parse("{\"marker\":\"ok\"}");
        return ValueTask.FromResult(document.RootElement.Clone());
    }

    public ValueTask<AgentSession> DeserializeAsync(JsonElement state, CancellationToken cancellationToken)
    {
        DeserializeCount++;
        if (ThrowOnDeserialize)
            throw new InvalidOperationException("快照反序列化失败（模拟损坏 / 版本不识别）");
        return ValueTask.FromResult(new Mock<AgentSession>().Object);
    }
}

/// <summary>
/// 只读观察 SessionStore 内部缓存（反射读 private 字段，不构造对象、不改生产代码）。
/// 字段与属性名来自 SessionStore.cs:50 (`Entry(AgentSession Session, DateTimeOffset LastAccess)`)
/// 与 :52 (`private readonly ConcurrentDictionary&lt;string, Entry&gt; _sessions`)。
/// </summary>
internal static class SessionStoreProbe
{
    private static object Dictionary(SessionStore store)
        => typeof(SessionStore)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(store)!;

    /// <summary>当前缓存的 sessionId 集合（按序数排序，便于断言具体值）。</summary>
    public static string[] Ids(SessionStore store)
    {
        var dict = Dictionary(store);
        var keys = (IEnumerable<string>)dict.GetType().GetProperty("Keys")!.GetValue(dict)!;
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    public static int Count(SessionStore store)
    {
        var dict = Dictionary(store);
        return (int)dict.GetType().GetProperty("Count")!.GetValue(dict)!;
    }

    /// <summary>读取某个 id 的 Entry（会话实例与最后访问时间）。</summary>
    public static (AgentSession Session, DateTimeOffset LastAccess) Entry(SessionStore store, string id)
    {
        var dict = Dictionary(store);
        var entry = dict.GetType().GetProperty("Item")!.GetValue(dict, new object[] { id })!;
        var session = (AgentSession)entry.GetType().GetProperty("Session")!.GetValue(entry)!;
        var last = (DateTimeOffset)entry.GetType().GetProperty("LastAccess")!.GetValue(entry)!;
        return (session, last);
    }
}

using AgentWorkbench.Memory.Persistence;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Session;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.CoreTests.Memory.Persistence;

/// <summary>
/// 长期记忆的结构化语义：覆盖键、作用域隔离、保留期、显式遗忘、排序与取库上限。
///
/// 这一块此前<b>一条行为测试都没有</b>——代码几百行、模型能直接调 <c>remember</c> / <c>recall</c>，
/// 却没人验过「写两次会不会变两条」「会不会串到别人的作用域」。
/// 本文件补的就是这两类：正例能绿，负控能红。
/// </summary>
public sealed class LongTermMemoryTests
{
    private static (SqliteLongTermMemoryStore Store, SqliteMemoryItemRepository Repository, FixedClock Clock, Action Cleanup)
        NewStore(Action<MemoryOptions>? configure = null)
    {
        var db = TempDatabase.Create(MemoryItemsSchema.Statements);
        var options = new MemoryOptions();
        configure?.Invoke(options);
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        var repository = new SqliteMemoryItemRepository(db.Factory);
        var store = new SqliteLongTermMemoryStore(repository, Options.Create(options), clock);
        return (store, repository, clock, db.Dispose);
    }

    private static readonly MemoryScope SessionA = MemoryScope.Session("s-a");
    private static readonly MemoryScope SessionB = MemoryScope.Session("s-b");

    // [L1] 同 (scope, key) 写两次 → 覆盖，不是新增；id / created_at / use_count 必须沿用。
    // 负控：把 UPSERT 改成「先删后插」→ id 与 created_at 变、use_count 归零，三条断言同时红。
    [Fact]
    public async Task L1_同作用域同覆盖键写两次_只留一条且标识与使用次数沿用()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            var first = await store.RememberAsync(new MemoryWrite(SessionA, "喜欢蓝色", Key: "color"));

            // 先召回一次，让 use_count 变成 1——用来证明「覆盖不会把用过很多次的记忆重置成新的」。
            await store.RecallAsync(new MemoryQuery(SessionA));

            var second = await store.RememberAsync(new MemoryWrite(SessionA, "喜欢深蓝色", Key: "color"));

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.CreatedAt, second.CreatedAt);
            Assert.Equal(1, second.UseCount);
            Assert.Equal("喜欢深蓝色", second.Content);
            Assert.Single(await store.RecallAsync(new MemoryQuery(SessionA)));
        }
        finally { cleanup(); }
    }

    // [L2] 不带 key 的写入 → 每次新增，绝不互相覆盖。
    // 负控：给空 key 也拼一个去重键（例如拿 content 当 key）→ 重复内容被合并，计数变 1，红。
    [Fact]
    public async Task L2_不带覆盖键的写入_每次都新增一条()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            await store.RememberAsync(new MemoryWrite(SessionA, "事实一"));
            await store.RememberAsync(new MemoryWrite(SessionA, "事实二"));

            Assert.Equal(2, (await store.RecallAsync(new MemoryQuery(SessionA))).Count);
        }
        finally { cleanup(); }
    }

    // [L3] 作用域隔离：另一个会话写的东西，本会话召不回来。
    // 负控：去掉 QueryAsync 里的 scope 过滤（只按 content 查）→ 两条都被召回，Assert.Single 红。
    [Fact]
    public async Task L3_跨会话召回不串数据()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            await store.RememberAsync(new MemoryWrite(SessionA, "A 的秘密"));
            await store.RememberAsync(new MemoryWrite(SessionB, "B 的秘密"));

            var a = await store.RecallAsync(new MemoryQuery(SessionA, IncludeGlobal: false));

            Assert.Single(a);
            Assert.Equal("A 的秘密", a[0].Content);
        }
        finally { cleanup(); }
    }

    // [L4] global 记忆能被任意作用域召回；关掉 IncludeGlobal 就看不到。
    // 负控：把 global 分支写成「scope_type = 'session'」→ 第一条断言红；
    //       把 IncludeGlobal 判断去掉（永远带上 global）→ 第二条断言红。
    [Fact]
    public async Task L4_全局记忆可被任意作用域召回_关掉开关就看不到()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            await store.RememberAsync(new MemoryWrite(MemoryScope.Global, "全公司都适用的偏好"));

            var withGlobal = await store.RecallAsync(new MemoryQuery(SessionA, IncludeGlobal: true));
            Assert.Contains(withGlobal, m => m.Content == "全公司都适用的偏好");

            var withoutGlobal = await store.RecallAsync(new MemoryQuery(SessionB, IncludeGlobal: false));
            Assert.DoesNotContain(withoutGlobal, m => m.Content == "全公司都适用的偏好");
        }
        finally { cleanup(); }
    }

    // [L5] 超长拒绝，且边界值（正好等于上限）必须能写。
    // 负控：删掉长度检查或把 > 写成 >= → 第一条不抛（或第二条误抛），红。
    [Fact]
    public async Task L5_超过字符上限直接拒绝_边界值可写()
    {
        var (store, _, _, cleanup) = NewStore(o => o.MaxMemoryChars = 10);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.RememberAsync(new MemoryWrite(SessionA, new string('x', 11))));

            // 边界正例：正好 10 个字符必须写得进去，否则「上限」被实现成了「小于」。
            await store.RememberAsync(new MemoryWrite(SessionA, new string('y', 10)));

            Assert.Single(await store.RecallAsync(new MemoryQuery(SessionA)));
        }
        finally { cleanup(); }
    }

    // [L6] 空内容（含全空白）拒绝；避免模型把「……」写进表里占位。
    // 负控：删掉 Trim 或空判断 → 不抛，红。
    [Fact]
    public async Task L6_空白内容拒绝()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.RememberAsync(new MemoryWrite(SessionA, "   ")));
        }
        finally { cleanup(); }
    }

    // [L7] 过保留期的记忆不再被召回（读路径判过期，不依赖后台清理）。
    // 负控：去掉 QueryAsync 的 `expires_at IS NULL OR expires_at > @Now` → 过期那条被召回，红。
    [Fact]
    public async Task L7_过保留期的记忆不再被召回()
    {
        var (store, _, clock, cleanup) = NewStore();
        try
        {
            await store.RememberAsync(new MemoryWrite(SessionA, "短期偏好", Retention: TimeSpan.FromMinutes(1)));

            clock.Advance(TimeSpan.FromMinutes(2));

            Assert.Empty(await store.RecallAsync(new MemoryQuery(SessionA)));
        }
        finally { cleanup(); }
    }

    // [L8] 显式遗忘：删掉了就召回不到；再删一次返回 false（幂等，不抛）。
    // 负控：把 DeleteAsync 改成恒返回 true → 第二次断言红；改成不删 → 第三条红。
    [Fact]
    public async Task L8_显式遗忘一条_再忘一次返回false()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            var item = await store.RememberAsync(new MemoryWrite(SessionA, "要忘掉的"));

            Assert.True(await store.ForgetAsync(item.Id));
            Assert.False(await store.ForgetAsync(item.Id));
            Assert.Empty(await store.RecallAsync(new MemoryQuery(SessionA)));
        }
        finally { cleanup(); }
    }

    // [L9] 召回按重要性降序；同重要性时按更新时间降序。
    // 负控：把 ORDER BY 改成 `importance ASC` → 第一条断言红。
    [Fact]
    public async Task L9_召回按重要性降序()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            await store.RememberAsync(new MemoryWrite(SessionA, "次要的", Importance: 0.2));
            await store.RememberAsync(new MemoryWrite(SessionA, "重要的", Importance: 0.9));

            var items = await store.RecallAsync(new MemoryQuery(SessionA));

            Assert.Equal("重要的", items[0].Content);
        }
        finally { cleanup(); }
    }

    // [L10] CandidateK 是「从库里取多少条」的上限，不是「注入几条」——截断发生在仓储层。
    // 负控：忽略 CandidateK（LIMIT 恒为默认值）→ 拿到 5 条，红。
    [Fact]
    public async Task L10_候选上限真的截断取库条数()
    {
        var (store, _, _, cleanup) = NewStore();
        try
        {
            for (var i = 0; i < 5; i++)
                await store.RememberAsync(new MemoryWrite(SessionA, $"第 {i} 条"));

            var items = await store.RecallAsync(new MemoryQuery(SessionA, CandidateK: 2));

            Assert.Equal(2, items.Count);
        }
        finally { cleanup(); }
    }

    // [L11] 召回会把命中行的 use_count +1、last_used_at 刷新（用于后续排序与「这条有用过」的观测）。
    // 负控：删掉 TouchAsync 调用 → use_count 停在 0，红。
    [Fact]
    public async Task L11_召回回写使用次数与最后使用时间()
    {
        var (store, repository, clock, cleanup) = NewStore();
        try
        {
            var item = await store.RememberAsync(new MemoryWrite(SessionA, "用得上的一条"));

            await store.RecallAsync(new MemoryQuery(SessionA));
            await store.RecallAsync(new MemoryQuery(SessionA));

            var row = await repository.GetAsync(item.Id);
            Assert.NotNull(row);
            Assert.Equal(2, row!.UseCount);
            Assert.Equal(
                clock.Now.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                row.LastUsedAt);
        }
        finally { cleanup(); }
    }
}

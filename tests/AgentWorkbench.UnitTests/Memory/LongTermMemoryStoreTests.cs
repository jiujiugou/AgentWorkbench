using System.Globalization;
using AgentWorkbench.Memory;
using AgentWorkbench.Memory.Persistence;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 长期记忆载体（SQLite 实现）的作用域 / 去重 / 保留期语义。
/// 每条判据都配一个负控：正例绿、负控必须能红。
/// </summary>
public sealed class LongTermMemoryStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-12T00:00:00Z", CultureInfo.InvariantCulture);

    private static readonly MemoryScope SessionA = MemoryScope.Session("session-a");
    private static readonly MemoryScope SessionB = MemoryScope.Session("session-b");

    [Fact]
    public async Task Recall_InSameScope_FindsWhatWasRemembered()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(SessionA, "用户偏好中文回答", Key: "lang"));

        var hits = await store.RecallAsync(new MemoryQuery(SessionA, "中文"));

        var item = Assert.Single(hits);
        Assert.Equal("用户偏好中文回答", item.Content);
        Assert.Equal("lang", item.Key);
        Assert.Equal(MemoryScopeTypes.Session, item.Scope.Type);
        Assert.Equal("session-a", item.Scope.Id);
    }

    [Fact]
    public async Task Recall_OtherScope_ReturnsEmpty()
    {
        // 负控：换一个作用域查，必须看不到别的会话写的记忆。
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(SessionA, "只属于会话 A 的秘密"));

        Assert.Empty(await store.RecallAsync(new MemoryQuery(SessionB, "秘密")));
    }

    [Fact]
    public async Task Remember_SameKey_OverwritesInsteadOfAdding()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        var first = await store.RememberAsync(new MemoryWrite(SessionA, "开口说中文", Key: "lang"));
        var second = await store.RememberAsync(new MemoryWrite(SessionA, "开口说英文", Key: "lang"));

        var hits = await store.RecallAsync(new MemoryQuery(SessionA));
        var item = Assert.Single(hits);
        Assert.Equal("开口说英文", item.Content);
        Assert.Equal(first.Id, second.Id);          // 覆盖，不是新增
        Assert.Equal(first.CreatedAt, item.CreatedAt);
        Assert.Equal(second.Id, item.Id);
    }

    [Fact]
    public async Task Remember_WithoutKey_AddsSeparateItems()
    {
        // 负控（对应去重）：不给 key 就必须是两条，不能被唯一索引误去重。
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(SessionA, "第一条"));
        await store.RememberAsync(new MemoryWrite(SessionA, "第二条"));

        var hits = await store.RecallAsync(new MemoryQuery(SessionA));
        Assert.Equal(2, hits.Count);
        Assert.All(hits, item => Assert.Null(item.Key));
    }

    [Fact]
    public async Task Recall_ExpiredItem_IsHidden()
    {
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(db, clock);

        await store.RememberAsync(new MemoryWrite(
            SessionA, "十天后作废", Key: "temp", Retention: TimeSpan.FromMinutes(10)));

        clock.Now = T0.AddMinutes(11);

        Assert.Empty(await store.RecallAsync(new MemoryQuery(SessionA)));
    }

    [Fact]
    public async Task Recall_NotExpiredItem_IsVisible()
    {
        // 负控（对应保留期）：没过期就必须照样召回到，证明上一条不是因为「写不进」而通过的。
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(db, clock);

        await store.RememberAsync(new MemoryWrite(
            SessionA, "还在保留期内", Key: "temp", Retention: TimeSpan.FromMinutes(10)));

        clock.Now = T0.AddMinutes(9);

        Assert.Single(await store.RecallAsync(new MemoryQuery(SessionA)));
    }

    [Fact]
    public async Task Recall_RespectsCandidateK_AndOrdersByImportance()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(SessionA, "低", Importance: 0.1));
        await store.RememberAsync(new MemoryWrite(SessionA, "高", Importance: 0.9));
        await store.RememberAsync(new MemoryWrite(SessionA, "中", Importance: 0.5));

        var hits = await store.RecallAsync(new MemoryQuery(SessionA, CandidateK: 2));

        Assert.Equal(["高", "中"], hits.Select(h => h.Content));
    }

    [Fact]
    public async Task Recall_RecordsUsage()
    {
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(db, clock);

        await store.RememberAsync(new MemoryWrite(SessionA, "会被用到", Key: "used"));
        clock.Now = T0.AddMinutes(5);

        var first = Assert.Single(await store.RecallAsync(new MemoryQuery(SessionA)));
        Assert.Equal(1, first.UseCount);
        Assert.Equal(clock.Now, first.LastUsedAt);

        var second = Assert.Single(await store.RecallAsync(new MemoryQuery(SessionA)));
        Assert.Equal(2, second.UseCount);   // 统计确实写回了库，而不是只在返回值里自增
    }

    [Fact]
    public async Task Recall_TextFilter_DoesNotTreatWildcardsAsMatchAll()
    {
        // 负控：查询里的 % 必须被当成字面量，否则召回会退化成「返回全部」。
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(SessionA, "普通内容"));
        await store.RememberAsync(new MemoryWrite(SessionA, "含百分号 100% 的内容"));

        var hits = await store.RecallAsync(new MemoryQuery(SessionA, "100%"));

        var item = Assert.Single(hits);
        Assert.Equal("含百分号 100% 的内容", item.Content);
    }

    [Fact]
    public async Task Recall_IncludesGlobal_ButOnlyWhenAsked()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(MemoryScope.Global, "全局事实"));
        await store.RememberAsync(new MemoryWrite(SessionA, "会话事实"));

        var withGlobal = await store.RecallAsync(new MemoryQuery(SessionA, IncludeGlobal: true));
        Assert.Equal(2, withGlobal.Count);

        var withoutGlobal = await store.RecallAsync(new MemoryQuery(SessionA, IncludeGlobal: false));
        var only = Assert.Single(withoutGlobal);
        Assert.Equal("会话事实", only.Content);
    }

    [Fact]
    public async Task Recall_FiltersByKind()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        await store.RememberAsync(new MemoryWrite(SessionA, "喜欢简洁", Kind: MemoryKinds.Preference));
        await store.RememberAsync(new MemoryWrite(SessionA, "项目叫 AgentWorkbench", Kind: MemoryKinds.Fact));

        var preferences = await store.RecallAsync(new MemoryQuery(SessionA, Kinds: [MemoryKinds.Preference]));
        Assert.Equal("喜欢简洁", Assert.Single(preferences).Content);

        // 负控：过滤一个没写过的类别必须是空。
        Assert.Empty(await store.RecallAsync(new MemoryQuery(SessionA, Kinds: [MemoryKinds.Decision])));
    }

    [Fact]
    public async Task Forget_RemovesItem_AndReportsMissForUnknownId()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        var item = await store.RememberAsync(new MemoryWrite(SessionA, "待遗忘"));

        Assert.True(await store.ForgetAsync(item.Id));
        Assert.Empty(await store.RecallAsync(new MemoryQuery(SessionA)));

        // 负控：再忘一次必须报 false，而不是假装成功。
        Assert.False(await store.ForgetAsync(item.Id));
    }

    [Fact]
    public async Task PurgeExpired_RemovesOnlyExpiredItems()
    {
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var store = NewStore(db, clock);

        await store.RememberAsync(new MemoryWrite(SessionA, "短命", Retention: TimeSpan.FromMinutes(1)));
        await store.RememberAsync(new MemoryWrite(SessionA, "长寿", Retention: TimeSpan.FromDays(30)));

        clock.Now = T0.AddMinutes(2);
        Assert.Equal(1, await store.PurgeExpiredAsync());

        var left = Assert.Single(await store.RecallAsync(new MemoryQuery(SessionA)));
        Assert.Equal("长寿", left.Content);
    }

    [Fact]
    public async Task Remember_RejectsBadInput()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0), new MemoryOptions { MaxMemoryChars = 10 });

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RememberAsync(new MemoryWrite(SessionA, "   ")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RememberAsync(new MemoryWrite(SessionA, new string('x', 11))));
        // 非 global 作用域必须带 id，否则「隔离」等于没有。
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RememberAsync(new MemoryWrite(new MemoryScope(MemoryScopeTypes.User, ""), "x")));
    }

    [Fact]
    public async Task Remember_ImportanceIsClamped()
    {
        using var db = new TempDatabase();
        var store = NewStore(db, new MutableTimeProvider(T0));

        var low = await store.RememberAsync(new MemoryWrite(SessionA, "低", Importance: -5));
        var high = await store.RememberAsync(new MemoryWrite(SessionA, "高", Importance: 5));

        Assert.Equal(0d, low.Importance);
        Assert.Equal(1d, high.Importance);
    }

    [Fact]
    public async Task Repository_Upsert_WithSameKey_KeepsSingleRow()
    {
        // 上层 RememberAsync 会「先查再沿用原 id」，那条路径盖住了 UPSERT 的冲突目标。
        // 这里故意传不同的 id，把冲突目标钉死在唯一索引 (scope_type, scope_id, mem_key) 上：
        // 若改成 ON CONFLICT(id) 会抛唯一约束错，若去掉唯一索引则会出现两行。
        using var db = new TempDatabase();
        var repository = new SqliteMemoryItemRepository(db.ConnectionFactory);

        await repository.UpsertAsync(Record("id-1", "偏好中文"));
        var stored = await repository.UpsertAsync(Record("id-2", "偏好英文"));

        var rows = await repository.QueryAsync(new MemoryItemQuery(
            MemoryScopeTypes.Session, "session-a", IncludeGlobal: false, Text: null, Kinds: null, Limit: 10,
            Now: Iso(T0)));

        var row = Assert.Single(rows);
        Assert.Equal("id-1", row.Id);          // 覆盖保留原行 id / created_at
        Assert.Equal("偏好英文", row.Content);
        Assert.Equal("id-1", stored.Id);
    }

    private static MemoryItemRecord Record(string id, string content) => new()
    {
        Id = id,
        ScopeType = MemoryScopeTypes.Session,
        ScopeId = "session-a",
        Kind = MemoryKinds.Fact,
        MemKey = "lang",
        Content = content,
        Importance = 0.5,
        CreatedAt = Iso(T0),
        UpdatedAt = Iso(T0),
        UseCount = 0
    };

    private static string Iso(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static SqliteLongTermMemoryStore NewStore(
        TempDatabase db, TimeProvider clock, MemoryOptions? options = null)
        => new(
            new SqliteMemoryItemRepository(db.ConnectionFactory),
            Options.Create(options ?? new MemoryOptions()),
            clock);
}

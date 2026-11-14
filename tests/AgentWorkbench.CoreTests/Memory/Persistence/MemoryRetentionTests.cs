using AgentWorkbench.Memory.Persistence;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.CoreTests.Memory.Persistence;

/// <summary>
/// 保留期清理（<see cref="MemoryRetentionService"/>）：过期会话行与过保留期的记忆行会被真删，
/// 未过期的一行不许动。
///
/// 为什么必须单独测：读路径也会判过期，所以「召回里看不到过期记忆」<b>不能</b>证明清理生效——
/// 那只证明查询带了过滤条件。这里的断言直接查仓储「这一行还在不在」。
/// </summary>
public sealed class MemoryRetentionTests
{
    private static readonly MemoryScope Scope = MemoryScope.Session("s-retention");

    private static (MemoryRetentionService Service, SqliteSessionStateRepository Sessions,
        SqliteLongTermMemoryStore Memory, SqliteMemoryItemRepository MemoryRepository,
        FixedClock Clock, Action Cleanup) NewService(Action<MemoryOptions>? configure = null)
    {
        var db = TempDatabase.Create(AgentSessionsSchema.Statements, MemoryItemsSchema.Statements);
        var options = new MemoryOptions { SessionTtlMinutes = 1440 };
        configure?.Invoke(options);
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));

        var sessions = new SqliteSessionStateRepository(db.Factory);
        var memoryRepository = new SqliteMemoryItemRepository(db.Factory);
        var memory = new SqliteLongTermMemoryStore(memoryRepository, Options.Create(options), clock);
        var service = new MemoryRetentionService(
            sessions, memory, Options.Create(options), clock,
            NullLogger<MemoryRetentionService>.Instance);

        return (service, sessions, memory, memoryRepository, clock, db.Dispose);
    }

    private static string Iso(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    // [R1] 过期会话行被删；未过期、以及不设过期（expires_at 为 null）的都保留。
    // 负控：把 DeleteExpiredAsync 的 `expires_at <= @now` 改成 `< @now` → 边界行留着，第二条断言红；
    //       改成删全表 → 后两条断言红。
    [Fact]
    public async Task R1_过期会话行被清_未过期与不过期的都保留()
    {
        var (service, sessions, _, _, clock, cleanup) = NewService();
        try
        {
            var now = Iso(clock.Now);
            await sessions.UpsertAsync(new SessionStateRecord
            {
                SessionId = "expired",
                StateJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = Iso(clock.Now.AddMinutes(-1)),
            });
            await sessions.UpsertAsync(new SessionStateRecord
            {
                SessionId = "live",
                StateJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = Iso(clock.Now.AddHours(1)),
            });
            await sessions.UpsertAsync(new SessionStateRecord
            {
                SessionId = "forever",
                StateJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = null,
            });

            await service.SweepOnceAsync();

            Assert.Null(await sessions.GetAsync("expired"));
            Assert.NotNull(await sessions.GetAsync("live"));
            Assert.NotNull(await sessions.GetAsync("forever"));
        }
        finally { cleanup(); }
    }

    // [R2] 过保留期的记忆行被真删（直接查仓储，不信召回的过滤），未过期的保留。
    // 负控：让 SweepOnceAsync 不调 memory.PurgeExpiredAsync → 第一条断言红；
    //       让 PurgeExpiredAsync 删全表 → 第二条断言红。
    [Fact]
    public async Task R2_过保留期的记忆行被真删_未过期的保留()
    {
        var (service, _, memory, repository, clock, cleanup) = NewService();
        try
        {
            var expired = await memory.RememberAsync(
                new MemoryWrite(Scope, "短期记忆", Retention: TimeSpan.FromMinutes(1)));
            var alive = await memory.RememberAsync(
                new MemoryWrite(Scope, "长期记忆", Retention: TimeSpan.FromDays(30)));

            clock.Advance(TimeSpan.FromMinutes(2));
            await service.SweepOnceAsync();

            // 关键：查仓储而不是查召回——召回本来就过滤 expires_at，看不到它证明不了清理动作。
            Assert.Null(await repository.GetAsync(expired.Id));
            Assert.NotNull(await repository.GetAsync(alive.Id));
        }
        finally { cleanup(); }
    }

    // [R3] SessionTtlMinutes = 0（不过期）时不扫会话行：即便表里有历史遗留的 expires_at，也不许删。
    // 负控：去掉 SweepOnceAsync 里的 `if (SessionTtlMinutes > 0)` 守卫 → 那行被删，断言红。
    [Fact]
    public async Task R3_关掉会话TTL时不清会话行()
    {
        var (service, sessions, _, _, clock, cleanup) = NewService(o => o.SessionTtlMinutes = 0);
        try
        {
            var now = Iso(clock.Now);
            await sessions.UpsertAsync(new SessionStateRecord
            {
                SessionId = "legacy",
                StateJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = Iso(clock.Now.AddMinutes(-1)),
            });

            await service.SweepOnceAsync();

            // TTL 关掉表示「会话不按时间过期」，那就不该有清理动作去动它。
            Assert.NotNull(await sessions.GetAsync("legacy"));
        }
        finally { cleanup(); }
    }
}

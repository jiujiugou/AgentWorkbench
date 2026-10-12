using System.Globalization;
using AgentWorkbench.Memory;
using AgentWorkbench.Memory.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 后台保留服务：过期会话行 / 过保留期记忆行必须被清掉，未过期的一条也不能动。
/// </summary>
public sealed class MemoryRetentionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-12T00:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Sweep_RemovesExpiredRows_AndKeepsLiveOnes()
    {
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = new MemoryOptions { SessionTtlMinutes = 10 };
        var sessionRepository = new SqliteSessionStateRepository(db.ConnectionFactory);
        var memoryRepository = new SqliteMemoryItemRepository(db.ConnectionFactory);
        var memoryStore = new SqliteLongTermMemoryStore(memoryRepository, Options.Create(options), clock);
        var sessions = new SqliteSessionStore(
            sessionRepository, Options.Create(options), clock, NullLogger<SqliteSessionStore>.Instance);

        // 记忆：短命 T0+10 过期，长寿 T0+60 过期。
        var shortLived = await memoryStore.RememberAsync(new MemoryWrite(
            MemoryScope.Session("s-1"), "短命", Retention: TimeSpan.FromMinutes(10)));
        var longLived = await memoryStore.RememberAsync(new MemoryWrite(
            MemoryScope.Session("s-1"), "长寿", Retention: TimeSpan.FromMinutes(60)));

        // 会话：s-1 在 T0 写（T0+10 过期），s-2 在 T0+5 写（T0+15 过期）。
        await SaveSessionAsync(sessions, "s-1");
        clock.Now = T0.AddMinutes(5);
        await SaveSessionAsync(sessions, "s-2");

        clock.Now = T0.AddMinutes(11);
        await NewService(sessionRepository, memoryStore, options, clock).SweepOnceAsync();

        Assert.Null(await sessionRepository.GetAsync("s-1"));
        Assert.NotNull(await sessionRepository.GetAsync("s-2"));
        Assert.Null(await memoryRepository.GetAsync(shortLived.Id));
        Assert.NotNull(await memoryRepository.GetAsync(longLived.Id));
    }

    [Fact]
    public async Task Sweep_BeforeAnythingExpires_DeletesNothing()
    {
        // 负控：时间没到就一条都不能删（证明上一条不是因为「无脑清表」而通过的）。
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = new MemoryOptions { SessionTtlMinutes = 10 };
        var sessionRepository = new SqliteSessionStateRepository(db.ConnectionFactory);
        var memoryRepository = new SqliteMemoryItemRepository(db.ConnectionFactory);
        var memoryStore = new SqliteLongTermMemoryStore(memoryRepository, Options.Create(options), clock);
        var sessions = new SqliteSessionStore(
            sessionRepository, Options.Create(options), clock, NullLogger<SqliteSessionStore>.Instance);

        await SaveSessionAsync(sessions, "s-1");
        var item = await memoryStore.RememberAsync(new MemoryWrite(
            MemoryScope.Session("s-1"), "还在期内", Retention: TimeSpan.FromMinutes(10)));

        clock.Now = T0.AddMinutes(1);
        await NewService(sessionRepository, memoryStore, options, clock).SweepOnceAsync();

        Assert.NotNull(await sessionRepository.GetAsync("s-1"));
        Assert.NotNull(await memoryRepository.GetAsync(item.Id));
    }

    private static MemoryRetentionService NewService(
        ISessionStateRepository sessions, ILongTermMemoryStore memory, MemoryOptions options, TimeProvider clock)
        => new(sessions, memory, Options.Create(options), clock, NullLogger<MemoryRetentionService>.Instance);

    private static async Task SaveSessionAsync(SqliteSessionStore store, string sessionId)
    {
        var factory = new CountingSessionFactory();
        var session = await factory.CreateAsync(CancellationToken.None);
        await store.SaveAsync(sessionId, factory, session);
    }
}

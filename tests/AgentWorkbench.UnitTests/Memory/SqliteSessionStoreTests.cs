using System.Globalization;
using AgentWorkbench.Memory;
using AgentWorkbench.Memory.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 落库版会话存储：用真实 MAF agent + 真实 SQLite（跑迁移）验证
/// 「重启后同一 sessionId 续上历史」这一条 P1-3 判据，并配负控。
/// </summary>
public sealed class SqliteSessionStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-12T00:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Restart_WithStoredSession_ContinuesHistory()
    {
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = Options.Create(new MemoryOptions { SessionEnabled = true, SessionTtlMinutes = 60 });

        // ── 第一次「进程」：跑两轮并落库 ──
        var client1 = new RecordingChatClient();
        var agent1 = BuildAgent(client1);
        var store1 = NewStore(db, options, clock);
        var session = await store1.GetOrCreateAsync("s-1", new AgentSessionFactory(agent1));
        await RunAsync(agent1, session, "hello");
        await RunAsync(agent1, session, "again");
        await store1.SaveAsync("s-1", new AgentSessionFactory(agent1), session);

        // ── 第二次「进程」：全新 agent + 全新 store，同一个库 ──
        var client2 = new RecordingChatClient();
        var agent2 = BuildAgent(client2);
        var store2 = NewStore(db, options, clock);
        var restored = await store2.GetOrCreateAsync("s-1", new AgentSessionFactory(agent2));
        await RunAsync(agent2, restored, "third");

        // 模型在新的进程里必须看到前两轮的 4 条历史 + 本轮输入。
        Assert.Equal(["hello", "ack-1", "again", "ack-2", "third"], client2.LastMessages);
    }

    [Fact]
    public async Task SameCase_WithTtlNotExpired_StillContinuesHistory()
    {
        // 正例对照：TTL 内访问必须续上（证明上一条不是因为「总是重放」而通过的）。
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = Options.Create(new MemoryOptions { SessionTtlMinutes = 10 });

        var client1 = new RecordingChatClient();
        var agent1 = BuildAgent(client1);
        var store1 = NewStore(db, options, clock);
        var session = await store1.GetOrCreateAsync("s-1", new AgentSessionFactory(agent1));
        await RunAsync(agent1, session, "hello");
        await store1.SaveAsync("s-1", new AgentSessionFactory(agent1), session);

        clock.Now = T0.AddMinutes(5);

        var client2 = new RecordingChatClient();
        var agent2 = BuildAgent(client2);
        var store2 = NewStore(db, options, clock);
        var restored = await store2.GetOrCreateAsync("s-1", new AgentSessionFactory(agent2));
        await RunAsync(agent2, restored, "second");

        Assert.Equal(["hello", "ack-1", "second"], client2.LastMessages);
    }

    [Fact]
    public async Task ExpiredStoredSession_IsTreatedAsNew()
    {
        // 负控：同一份库、同一 sessionId，只把时钟推过 TTL——历史必须丢。
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = Options.Create(new MemoryOptions { SessionTtlMinutes = 10 });

        var client1 = new RecordingChatClient();
        var agent1 = BuildAgent(client1);
        var store1 = NewStore(db, options, clock);
        var session = await store1.GetOrCreateAsync("s-1", new AgentSessionFactory(agent1));
        await RunAsync(agent1, session, "hello");
        await store1.SaveAsync("s-1", new AgentSessionFactory(agent1), session);

        clock.Now = T0.AddMinutes(11);

        var client2 = new RecordingChatClient();
        var agent2 = BuildAgent(client2);
        var store2 = NewStore(db, options, clock);
        var restored = await store2.GetOrCreateAsync("s-1", new AgentSessionFactory(agent2));
        await RunAsync(agent2, restored, "second");

        Assert.Equal(["second"], client2.LastMessages);
    }

    [Fact]
    public async Task UnknownSessionId_DoesNotReadAnotherSessionsHistory()
    {
        // 负控：换个不存在的 sessionId 必须新建，绝不能复用别人的历史。
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = Options.Create(new MemoryOptions { SessionTtlMinutes = 60 });

        var seeded = BuildAgent(new RecordingChatClient());
        var store = NewStore(db, options, clock);
        var first = await store.GetOrCreateAsync("s-1", new AgentSessionFactory(seeded));
        await RunAsync(seeded, first, "secret");
        await store.SaveAsync("s-1", new AgentSessionFactory(seeded), first);

        var client = new RecordingChatClient();
        var agent = BuildAgent(client);
        var fresh = await store.GetOrCreateAsync("s-2", new AgentSessionFactory(agent));
        await RunAsync(agent, fresh, "unrelated");

        Assert.Equal(["unrelated"], client.LastMessages);
    }

    [Fact]
    public async Task CorruptSnapshot_FallsBackToNewSession()
    {
        // 快照损坏 / MAF 版本不兼容时不能让会话永久卡死：丢掉重来。
        using var db = new TempDatabase();
        var clock = new MutableTimeProvider(T0);
        var options = Options.Create(new MemoryOptions { SessionTtlMinutes = 60 });
        var repository = new SqliteSessionStateRepository(db.ConnectionFactory);
        await repository.UpsertAsync(new SessionStateRecord
        {
            SessionId = "s-1",
            StateJson = "{ not json",
            CreatedAt = T0.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            UpdatedAt = T0.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ExpiresAt = null
        });

        var client = new RecordingChatClient();
        var agent = BuildAgent(client);
        var store = new SqliteSessionStore(repository, options, clock, NullLogger<SqliteSessionStore>.Instance);
        var session = await store.GetOrCreateAsync("s-1", new AgentSessionFactory(agent));
        await RunAsync(agent, session, "hi");

        Assert.Equal(["hi"], client.LastMessages);
        Assert.Null(await repository.GetAsync("s-1"));   // 坏行已被清掉
    }

    private static SqliteSessionStore NewStore(
        TempDatabase db, IOptions<MemoryOptions> options, TimeProvider clock)
        => new(new SqliteSessionStateRepository(db.ConnectionFactory), options, clock,
            NullLogger<SqliteSessionStore>.Instance);

    private static ChatClientAgent BuildAgent(IChatClient client)
        => new(client, new ChatClientAgentOptions
        {
            Name = "memory-test",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions())
        });

    private static Task RunAsync(ChatClientAgent agent, AgentSession session, string text)
        => agent.RunAsync(
            new[] { new ChatMessage(ChatRole.User, text) }, session,
            new ChatClientAgentRunOptions(), CancellationToken.None);

    /// <summary>确定性模型：记录「这次请求看到了哪些消息」，并回一句 <c>ack-N</c>。</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private int _calls;

        public IReadOnlyList<string> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.Select(message => message.Text ?? string.Empty).ToList();
            _calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"ack-{_calls}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("会话续存用例只走非流式路径");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

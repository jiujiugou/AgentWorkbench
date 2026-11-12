using AgentWorkbench.TestKit;
using AgentWorkbench.Memory.Persistence.ChatHistory;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Session;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.CoreTests.Memory.Persistence;

/// <summary>
/// 对话历史逐条落库（取代「整个会话快照覆盖一行」）。
///
/// 这里验的是<b>案例一</b>那个失效模式：同一会话被两个 run 同时推进时，
/// 旧实现下后完成的 run 会用整份快照盖掉先完成的那轮，模型下一轮读不到自己刚办过的事。
/// 改成逐条追加后，两边各写自己的行，读回时都在。
///
/// 为什么用 <c>Task.WhenAll</c> 但不强求顺序：追加式没有「覆盖」这个动作，
/// 所以断言的是「都在」；顺序由 C2 用串行场景单独验。
///
/// 建表仍走 <see cref="ChatHistorySchema.Statements"/>（与 M031 共用一份），
/// 读写走 EF Core（与生产同一条路径）。
/// </summary>
public sealed class ChatHistoryPersistenceTests
{
    private static (IDbContextFactory<AppDbContext> Contexts, Action Cleanup) NewDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aw-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "chat.db"),
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                pragma.ExecuteNonQuery();
            }

            foreach (var statement in ChatHistorySchema.Statements)
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }

        // 与生产注册保持一致：同一个 Sqlite 文件 + snake_case 约定（列名靠它才叫 session_id）。
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(options => options
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention());
        var contexts = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();

        return (contexts, () =>
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* 临时目录清理失败不影响结论 */ }
        });
    }

    /// <summary>
    /// 每个 run 一个 agent 实例——harness 就是这么做的（每次 run 重新装配）。
    /// <c>RepeatLast</c>：同一个 agent 连跑两轮时，第二轮不应退化成「脚本用尽」的兜底文案。
    /// </summary>
    private static ChatClientAgent BuildAgent(ChatHistoryProvider provider, string replyText)
    {
        var llm = new ScriptedChatClient { RepeatLast = true }.Reply(replyText);
        return new ChatClientAgent(llm, new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions()
        });
    }

    /// <summary>造一个已绑定 sessionId 的会话——真实链路由 Harness 在 PrepareRunAsync 里写这个键。</summary>
    private static async Task<AgentSession> BindSession(ChatClientAgent agent, string sessionId)
    {
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(AgentSessionKeys.SessionId, sessionId, null!);
        return session;
    }

    // [C1] 两个 run 并发推进同一会话 → 两轮的消息都在，谁也没盖掉谁。
    // 负控：把 AppendAsync 改成「按 session_id 先删再插」（退化为覆盖写）
    //       → 本用例会只剩一边的消息，Assert 红。
    [Fact]
    public async Task C1_两个run并发推进同一会话_两轮消息都保留()
    {
        var (contexts, cleanup) = NewDatabase();
        try
        {
            var store = new SqliteChatMessageStore(contexts);
            var provider = new SqliteChatHistoryProvider(store, NullLogger<SqliteChatHistoryProvider>.Instance);
            var sessionId = "session-concurrent";

            var first = BuildAgent(provider, "答复一");
            var second = BuildAgent(provider, "答复二");
            var sessionA = await BindSession(first, sessionId);
            var sessionB = await BindSession(second, sessionId);

            await Task.WhenAll(
                first.RunAsync("第一句", sessionA),
                second.RunAsync("第二句", sessionB));

            var history = await store.LoadAsync(sessionId);

            Assert.Equal(4, history.Count);
            Assert.Contains(history, m => m.Text == "第一句");
            Assert.Contains(history, m => m.Text == "答复一");
            Assert.Contains(history, m => m.Text == "第二句");
            Assert.Contains(history, m => m.Text == "答复二");
        }
        finally
        {
            cleanup();
        }
    }

    // [C2] 串行两轮 → 读回顺序与写入顺序一致（按自增 id，而不是时间戳）。
    // 负控：LoadAsync 改成 OrderBy(CreatedAt) —— 同批消息时间戳相同，顺序会退化，本用例红。
    [Fact]
    public async Task C2_历史按追加顺序读回()
    {
        var (contexts, cleanup) = NewDatabase();
        try
        {
            var store = new SqliteChatMessageStore(contexts);
            var provider = new SqliteChatHistoryProvider(store, NullLogger<SqliteChatHistoryProvider>.Instance);
            var agent = BuildAgent(provider, "答复");
            var session = await BindSession(agent, "session-ordered");

            await agent.RunAsync("第一句", session);
            await agent.RunAsync("第二句", session);

            var history = await store.LoadAsync("session-ordered");

            Assert.Equal(new[] { "第一句", "答复", "第二句", "答复" }, history.Select(m => m.Text).ToArray());
        }
        finally
        {
            cleanup();
        }
    }

    // [C3] 会话隔离：按 sessionId 读，读不到别人的历史。
    // 负控：Where 去掉 SessionId 条件 → 本用例会读到 4 条，Assert 红。
    [Fact]
    public async Task C3_不同会话的历史互不可见()
    {
        var (contexts, cleanup) = NewDatabase();
        try
        {
            var store = new SqliteChatMessageStore(contexts);
            var provider = new SqliteChatHistoryProvider(store, NullLogger<SqliteChatHistoryProvider>.Instance);

            var agentX = BuildAgent(provider, "X 的答复");
            var agentY = BuildAgent(provider, "Y 的答复");
            var sessionX = await BindSession(agentX, "session-x");
            var sessionY = await BindSession(agentY, "session-y");

            await agentX.RunAsync("X 的问题", sessionX);
            await agentY.RunAsync("Y 的问题", sessionY);

            var historyX = await store.LoadAsync("session-x");
            var historyY = await store.LoadAsync("session-y");

            Assert.Equal(2, historyX.Count);
            Assert.Equal(2, historyY.Count);
            Assert.All(historyX, m => Assert.DoesNotContain("Y", m.Text ?? string.Empty));
            Assert.All(historyY, m => Assert.DoesNotContain("X", m.Text ?? string.Empty));
        }
        finally
        {
            cleanup();
        }
    }

    // [C4] 没有 sessionId 的会话不落库，也不会抛异常——一次性 run（无记忆）必须仍然可用。
    // 负控：ResolveSessionId 改成「取不到就抛」→ 本用例红。
    [Fact]
    public async Task C4_没有会话标识时不落库也不报错()
    {
        var (contexts, cleanup) = NewDatabase();
        try
        {
            var store = new SqliteChatMessageStore(contexts);
            var provider = new SqliteChatHistoryProvider(store, NullLogger<SqliteChatHistoryProvider>.Instance);
            var agent = BuildAgent(provider, "答复");
            var session = await agent.CreateSessionAsync();   // 刻意不写 sessionId

            await agent.RunAsync("无会话标识的一句话", session);

            Assert.Empty(await store.LoadAsync("session-any"));
        }
        finally
        {
            cleanup();
        }
    }

    // [C5] 空批次是 no-op：不建 DbContext、不 SaveChanges。
    [Fact]
    public async Task C5_追加空批次是noop()
    {
        var (contexts, cleanup) = NewDatabase();
        try
        {
            var store = new SqliteChatMessageStore(contexts);
            await store.AppendAsync("session-empty", Array.Empty<ChatMessage>());
            Assert.Empty(await store.LoadAsync("session-empty"));
        }
        finally
        {
            cleanup();
        }
    }
}

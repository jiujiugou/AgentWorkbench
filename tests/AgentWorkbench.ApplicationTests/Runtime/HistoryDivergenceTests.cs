using AgentWorkbench.ApplicationTests.Support;
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

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// 事实锁定：两个职责各走一条路径，形状不同是**正常结果**，不是缺陷。
///
/// - `messages`（`conversations` 的子表）：界面投影，只有 user / assistant 文本，由 `AgentHub` 写。
/// - `chat_messages`：模型上下文（含 functionCall / functionResult），由 provider 写。
///
/// 这几条不是「验功能」，是把两条路径的形状钉住，防止有人以为它们是同一份数据。
/// X1/X4 属于职责不同的必然结果；X2/X3 对应 `docs/core/runtime.md` §7.3 第 1 条
/// 那两个尚未定义的边界条件（失败轮、删除/TTL），改之前先定边界。
///
/// 写路径刻意照抄 `AgentHub`：user 消息在 run <b>之前</b>写（Hub:133），
/// assistant 只在收尾时写（Hub:214 → `SaveAnswerAsync`，空答复不写）。
/// </summary>
public sealed class HistoryDivergenceTests
{
    private sealed class LookupTool
    {
        public string LookupOrder(string id) => "PO-" + id;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required IDbContextFactory<AppDbContext> Contexts { get; init; }
        public required ConversationRepository Conversations { get; init; }
        public required SqliteChatMessageStore Store { get; init; }
        public required string Root { get; init; }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 临时目录清理失败不影响结论 */ }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 建三张表：`conversations` / `messages`（照 M016 + M025）与 `chat_messages`（照 M031）。
    ///
    /// `ForeignKeys=True` 是生产行为（<c>StoragePaths</c> 写进连接串），
    /// 少了它 `DeleteAsync` 不会级联删 `messages`，X3 就验不出真实形状。
    /// </summary>
    private static async Task<Fixture> NewFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aw-diverge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "divergence.db"),
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
            ForeignKeys = true,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            foreach (var statement in Statements())
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync();
            }
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(options => options
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention());
        var contexts = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();

        return new Fixture
        {
            Contexts = contexts,
            Conversations = new ConversationRepository(contexts, NullLogger<ConversationRepository>.Instance),
            Store = new SqliteChatMessageStore(contexts),
            Root = root,
        };
    }

    private static IEnumerable<string> Statements()
    {
        yield return """
            CREATE TABLE IF NOT EXISTS conversations (
                id         TEXT NOT NULL PRIMARY KEY,
                title      TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                tenant_id  TEXT NULL,
                user_id    TEXT NULL
            )
            """;
        yield return """
            CREATE TABLE IF NOT EXISTS messages (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role            TEXT NOT NULL,
                content         TEXT NOT NULL,
                created_at      TEXT NOT NULL
            )
            """;
        foreach (var statement in ChatHistorySchema.Statements)
            yield return statement;
    }

    private static ChatClientAgent BuildAgent(ChatHistoryProvider provider, ScriptedChatClient llm)
        => BuildAgent(provider, (IChatClient)new FunctionInvokingChatClient(llm), withTools: true);

    private static ChatClientAgent BuildAgent(ChatHistoryProvider provider, IChatClient pipeline, bool withTools)
        => new(pipeline, new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions
            {
                Tools = withTools
                    ? [AIFunctionFactory.Create(new LookupTool().LookupOrder, "LookupOrder")]
                    : []
            }
        });

    /// <summary>
    /// 复刻生产里 <c>IContextAssembler.Wrap</c> 的形状：在 Agent 内部、provider 下游插一条 system 消息。
    /// 生产类 <c>ContextInjectingChatClient</c> 是 internal，测试里按同一形状写一份。
    /// </summary>
    private sealed class SystemInjector(IChatClient inner, string instructions) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => inner.GetResponseAsync(With(messages), options, ct);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => inner.GetStreamingResponseAsync(With(messages), options, ct);

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();

        private IEnumerable<ChatMessage> With(IEnumerable<ChatMessage> messages)
            => [new ChatMessage(ChatRole.System, instructions), .. messages];
    }

    private static async Task<AgentSession> BindSessionAsync(ChatClientAgent agent, string sessionId)
    {
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(AgentSessionKeys.SessionId, sessionId, null!);
        return session;
    }

    // [X1] 一轮带工具调用的对话：界面 2 条，模型 4 条；工具调用只存在于模型那一份。
    // 这是「界面不展示执行过程」的正常结果，不是分歧缺陷；见 docs/core/runtime.md §7.1。
    [Fact]
    public async Task X1_一轮带工具的对话_两个源条数不同且工具只在一个源里()
    {
        await using var db = await NewFixtureAsync();
        var provider = new SqliteChatHistoryProvider(db.Store, NullLogger<SqliteChatHistoryProvider>.Instance);

        var conversation = await db.Conversations.CreateAsync("tenant-a", "user-1");
        await db.Conversations.AddMessageAsync(conversation.Id, "user", "帮我查一下这单");

        var llm = new ScriptedChatClient()
            .ToolCall("LookupOrder", new Dictionary<string, object?> { ["id"] = "1" })
            .Reply("这单已经发货了");

        var agent = BuildAgent(provider, llm);
        var session = await BindSessionAsync(agent, conversation.Id);
        await agent.RunAsync("帮我查一下这单", session);

        await db.Conversations.AddMessageAsync(conversation.Id, "assistant", "这单已经发货了");

        var ui = await db.Conversations.GetMessagesAsync(conversation.Id);
        var model = await db.Store.LoadAsync(conversation.Id);

        Assert.Equal(new[] { "user", "assistant" }, ui.Select(m => m.Role).ToArray());
        Assert.Equal(4, model.Count);

        // 界面那一份只有文本：看不到模型调过 LookupOrder，也看不到工具返回了什么。
        Assert.DoesNotContain(ui, m => m.Content.Contains("LookupOrder", StringComparison.Ordinal));
        Assert.DoesNotContain(ui, m => m.Content.Contains("PO-1", StringComparison.Ordinal));

        // 模型那一份有完整往返。
        Assert.Contains(model, m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(model, m => m.Contents.OfType<FunctionResultContent>().Any());
    }

    // [X2] 失败的 run：界面留下一条没有答复的提问；模型历史里没有任何 assistant 消息。
    // 这就是「失败轮污染会话」的形状——Hub 先写 user（:133），答复为空时 SaveAnswerAsync 直接 return（:356）。
    [Fact]
    public async Task X2_失败的run_界面留下孤立提问_模型历史里没有答复()
    {
        await using var db = await NewFixtureAsync();
        var provider = new SqliteChatHistoryProvider(db.Store, NullLogger<SqliteChatHistoryProvider>.Instance);

        var conversation = await db.Conversations.CreateAsync("tenant-a", "user-1");
        await db.Conversations.AddMessageAsync(conversation.Id, "user", "这句话会让模型报错");

        var llm = new ScriptedChatClient().Fail(new InvalidOperationException("model 500"));
        var agent = BuildAgent(provider, llm);
        var session = await BindSessionAsync(agent, conversation.Id);

        await Assert.ThrowsAnyAsync<Exception>(() => agent.RunAsync("这句话会让模型报错", session));

        var ui = await db.Conversations.GetMessagesAsync(conversation.Id);
        var model = await db.Store.LoadAsync(conversation.Id);

        var only = Assert.Single(ui);
        Assert.Equal("user", only.Role);

        // 实测：模型那一份是**空**的——失败时 provider 的 Store 一次都没被调用（含 user 也没落）。
        // 所以失败轮在两个源里的形状完全不同：界面留了一条孤问，模型连「用户问过」都不知道。
        // 负控：把 StoreChatHistoryAsync 改成无条件下落库 → 本用例红（model 非空）。
        Assert.Empty(model);
    }

    // [X3] 删除会话：`messages` 随外键级联删掉，`chat_messages` 无外键、原样留着。
    // 后果：用户以为删干净了，模型下一轮还记着这段对话（§7.3 第 9 条）。
    [Fact]
    public async Task X3_删除会话_界面历史消失_模型历史留下()
    {
        await using var db = await NewFixtureAsync();
        var provider = new SqliteChatHistoryProvider(db.Store, NullLogger<SqliteChatHistoryProvider>.Instance);

        var conversation = await db.Conversations.CreateAsync("tenant-a", "user-1");
        await db.Conversations.AddMessageAsync(conversation.Id, "user", "第一句");

        var agent = BuildAgent(provider, new ScriptedChatClient().Reply("答复"));
        var session = await BindSessionAsync(agent, conversation.Id);
        await agent.RunAsync("第一句", session);
        await db.Conversations.AddMessageAsync(conversation.Id, "assistant", "答复");

        Assert.True(await db.Conversations.DeleteAsync(conversation.Id, "tenant-a", "user-1"));

        Assert.Empty(await db.Conversations.GetMessagesAsync(conversation.Id));
        Assert.NotEmpty(await db.Store.LoadAsync(conversation.Id));
    }

    // [X4] 注入的 system 指令不落历史：模型看到了，历史里没有。
    // 生产的 `IContextAssembler.Wrap` 就装在 provider 下游（ContextInjectingChatClient 的注释明写
    // 「指令不随每轮对话在历史里累积」）。后果：光看 chat_messages 复原不出「模型当时看到了什么」。
    [Fact]
    public async Task X4_注入的system指令进模型请求但不进历史()
    {
        await using var db = await NewFixtureAsync();
        var provider = new SqliteChatHistoryProvider(db.Store, NullLogger<SqliteChatHistoryProvider>.Instance);

        var conversation = await db.Conversations.CreateAsync("tenant-a", "user-1");
        var llm = new ScriptedChatClient().Reply("答复");
        var agent = BuildAgent(
            provider,
            new SystemInjector(new FunctionInvokingChatClient(llm), "你是 ERP 采购助手"),
            withTools: false);

        var session = await BindSessionAsync(agent, conversation.Id);
        await agent.RunAsync("第一句", session);

        var model = await db.Store.LoadAsync(conversation.Id);

        // 模型确实收到了 system 指令……
        Assert.Contains(llm.LastRequest, m => m.Role == ChatRole.System);
        // ……但历史里一条都没有。
        Assert.DoesNotContain(model, m => m.Role == ChatRole.System);
        Assert.Equal(2, model.Count);
    }
}

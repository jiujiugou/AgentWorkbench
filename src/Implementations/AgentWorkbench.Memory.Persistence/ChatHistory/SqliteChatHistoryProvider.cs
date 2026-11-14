using AgentWorkbench.Runtime.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Memory.Persistence.ChatHistory;

/// <summary>
/// 把对话历史逐条落 SQLite 的 <see cref="ChatHistoryProvider"/>。
///
/// 它替换掉的是 <c>InMemoryChatHistoryProvider</c> + 「整个会话序列化后覆盖 <c>agent_sessions</c> 一行」
/// 那条路。换掉之后：
/// - 历史只有「追加」，没有「覆盖」，同一会话被两个 run 并发推进也不会互相盖掉；
/// - <c>agent_sessions</c> 不再承载历史，快照里只剩非历史状态，丢失的伤害随之消失。
///
/// 三个刻意的取舍：
/// 1. <b>sessionId 不放实例字段</b>。一个 provider 服务所有会话，把 sessionId 存进字段必然串号；
///    官方要求 session 相关状态放 <c>AgentSession.StateBag</c>，由编排层在每次 run 前写入。
/// 2. <b>不开 <c>RequirePerServiceCallChatHistoryPersistence</c></b>。实测（HistoryProviderProbeTests.H1）
///    一次 run 结束时工具调用与工具结果都已在历史里，开那个开关只会让
///    FunctionInvokingChatClient 改变行为，收益为零、风险非零。
/// 3. <b>不做裁剪</b>。取多少条由 Context 决定，provider 只负责如实读回。
///
/// 口径、红线与「出问题先看哪」见 <c>docs/core/状态与记忆.md</c>；改上面三条取舍时同步那份文档。
/// </summary>
public sealed class SqliteChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>
    /// sessionId 在 <c>AgentSession.StateBag</c> 里的键。MAF 的 <c>AgentSession</c> 本身不带 id，
    /// 所以这个值必须由编排层写入，provider 只读。键名的唯一定义在 <see cref="AgentSessionKeys"/>。
    /// </summary>
    public const string SessionIdKey = AgentSessionKeys.SessionId;

    private readonly IChatMessageStore _store;
    private readonly ILogger<SqliteChatHistoryProvider> _logger;

    public SqliteChatHistoryProvider(
        IChatMessageStore store,
        ILogger<SqliteChatHistoryProvider> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>本 provider 在 StateBag 里占用哪些键——序列化时据此保存/还原。</summary>
    public override IReadOnlyList<string> StateKeys => [SessionIdKey];

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = ResolveSessionId(context.Session);
        if (sessionId is null)
        {
            // 没有 sessionId 就查不到历史：当空历史处理而不是抛异常。
            // 抛异常会让「不用会话的一次性 run」直接不可用，而它本来就该没有历史。
            _logger.LogDebug("本次调用没有 sessionId，按空历史处理");
            return [];
        }

        return await _store.LoadAsync(sessionId, cancellationToken);
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = ResolveSessionId(context.Session);
        if (sessionId is null)
        {
            _logger.LogDebug("本次调用没有 sessionId，历史不落库");
            return;
        }

        // 实测形状：RequestMessages = 本次输入，ResponseMessages = 本轮产出（含工具调用与工具结果）。
        // 两者拼起来正好是本轮新增的全部消息，不含旧历史——不会重复写。
        var incoming = context.RequestMessages.Concat(context.ResponseMessages).ToList();
        await _store.AppendAsync(sessionId, incoming, cancellationToken);
    }

    private static string? ResolveSessionId(AgentSession session)
    {
        var value = session.StateBag.GetValue<string>(SessionIdKey, null!);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

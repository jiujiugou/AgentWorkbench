using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentWorkbench.AgentHarness;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Knowledge.Local.Tools;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.ToolExecution;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Webapi.Hubs;

/// <summary>客户端发起的对话请求。</summary>
public sealed record ChatRequest(string? ConversationId, string Text, List<ChatTurn>? History);

/// <summary>
/// Agent 实时通道：接收用户消息，流式推送 token，并汇报工具调用事件。
/// 消息历史持久化到数据库，刷新/切换后可恢复。
/// </summary>
/// <remarks>
/// 两条入口（普通对话 <see cref="Send"/> / 知识问答 <see cref="Ask"/>）走的是<b>同一条 harness 链路</b>：
/// 同一 loop、同一执行控制、同一轨迹落库，差别只在注入的系统指令。
/// 此前这里绕过 harness 直连对话 / 检索服务，于是「用户实际在用的这条路」既没有轨迹也没有预算，
/// 评测一条都覆盖不到——这正是「验收假绿」的结构性来源。
/// 来源面板改由工具完成事件驱动（模型调 <c>search_knowledge</c> 时推送），不再依赖服务端预检索。
/// </remarks>
public sealed class AgentHub : Hub
{
    private readonly IAgentHarness _harness;
    private readonly IConversationRepository _conversations;
    private readonly ProviderOptions _providerOptions;
    private readonly ILogger<AgentHub> _logger;

    /// <summary>每个连接当前的生成任务，用于"停止生成"与断线取消。</summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Generations = new();

    public AgentHub(
        IAgentHarness harness,
        IConversationRepository conversations,
        IOptions<ProviderOptions> providerOptions,
        ILogger<AgentHub> logger)
    {
        _harness = harness;
        _conversations = conversations;
        _providerOptions = providerOptions.Value;
        _logger = logger;
    }

    /// <summary>发送一条消息并流式接收回复。</summary>
    public Task Send(ChatRequest request) => RunAsync(request, knowledgeMode: false);

    /// <summary>知识问答：模型需要依据时会调用 search_knowledge，命中来源随工具完成事件推送。</summary>
    public Task Ask(ChatRequest request) => RunAsync(request, knowledgeMode: true);

    /// <summary>
    /// 两条入口的共同实现：建/取会话 → 起一次 harness 流 → 按事件推进前端。
    /// </summary>
    private async Task RunAsync(ChatRequest request, bool knowledgeMode)
    {
        var messageId = Guid.NewGuid().ToString("N");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        Generations[Context.ConnectionId] = linked;

        var answer = new StringBuilder();
        ConversationRecord? conversation = null;

        try
        {
            // 先探活：模型没配就当场告诉用户，不必白跑一次 run、白落一条失败轨迹。
            var capabilities = await _harness.GetCapabilitiesAsync(linked.Token);
            if (!capabilities.Llm)
            {
                await SendErrorAsync("模型未配置：请在「设置」页添加供应商与模型。");
                return;
            }

            conversation = await _conversations.GetOrCreateAsync(request.ConversationId);
            await Clients.Caller.SendAsync("ReceiveConversation", new { conversationId = conversation.Id }, linked.Token);
            await _conversations.AddMessageAsync(conversation.Id, "user", request.Text);

            // conversationId 同时就是 harness 的 sessionId：一个会话一份跨 run 记忆。
            // 消息正文另存对话表，专供界面恢复（会话快照是模型记忆，两者职责不同，不重复读）。
            var runRequest = new HarnessRunRequest(
                conversation.Id, request.Text, ResolveInstructions(knowledgeMode));

            await foreach (var evt in _harness.StreamAsync(runRequest, linked.Token))
            {
                switch (evt.Kind)
                {
                    case HarnessStreamEventKind.TextDelta:
                        answer.Append(evt.Text);
                        await Clients.Caller.SendAsync("ReceiveDelta", new { messageId, delta = evt.Text }, linked.Token);
                        break;

                    case HarnessStreamEventKind.ToolCompleted:
                        var sources = TryReadKnowledgeSources(evt.Tool);
                        if (sources is not null)
                            await Clients.Caller.SendAsync("ReceiveSources", new { messageId, sources }, linked.Token);
                        break;

                    case HarnessStreamEventKind.Failed:
                        // 失败已由 harness 落轨迹（含失败分类码），这里只负责让用户看见。
                        await SendErrorAsync(evt.Error ?? "生成失败。");
                        break;

                    case HarnessStreamEventKind.Completed:
                        // 终态由下面统一收尾（保存答复 + ReceiveDone），此处不重复动作。
                        break;
                }
            }

            await SaveAnswerAsync(conversation.Id, answer);
            await Clients.Caller.SendAsync("ReceiveDone", new { messageId, conversationId = conversation.Id }, linked.Token);
        }
        catch (OperationCanceledException)
        {
            await SaveAnswerAsync(conversation?.Id, answer);
            await SendErrorAsync("已停止生成。");
        }
        catch (Exception ex)
        {
            await SaveAnswerAsync(conversation?.Id, answer);
            _logger.LogError(ex, "生成失败：knowledgeMode={KnowledgeMode}", knowledgeMode);
            await SendErrorAsync(ex.Message);
        }
        finally
        {
            Generations.TryRemove(Context.ConnectionId, out _);
        }
    }

    /// <summary>本次 run 的系统指令：知识模式用检索-引用规范，普通模式用设置页里的系统提示词。</summary>
    private string? ResolveInstructions(bool knowledgeMode)
    {
        if (knowledgeMode)
            return KnowledgeInstructions.AnswerWithCitations;

        var prompt = _providerOptions.SystemPrompt;
        return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
    }

    /// <summary>
    /// 把 <c>search_knowledge</c> 的工具结果解析成来源列表；不是该工具或解析失败时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 来源面板是增益而非主链路：工具输出格式变了、被截断、或不是知识工具，都不该让整条对话挂掉。
    /// 空命中也不推面板——弹一个空的「来源」比不弹更让人困惑。
    /// </remarks>
    private IReadOnlyList<object>? TryReadKnowledgeSources(ToolCallTrace? trace)
    {
        if (trace is null
            || !string.Equals(trace.Name, SearchKnowledgeTool.ToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(trace.Result))
            return null;

        try
        {
            var hits = JsonSerializer.Deserialize<List<RetrievalHit>>(trace.Result, SearchKnowledgeTool.SerializerOptions);
            return hits is null or [] ? null : hits.Select(ToSource).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "解析 search_knowledge 结果失败，本次不推送来源");
            return null;
        }
    }

    private async Task SaveAnswerAsync(string? conversationId, StringBuilder answer)
    {
        if (string.IsNullOrEmpty(conversationId) || answer.Length == 0)
            return;
        try
        {
            await _conversations.AddMessageAsync(conversationId, "assistant", answer.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存助手消息失败：{ConversationId}", conversationId);
        }
    }

    private Task SendErrorAsync(string message)
        => Clients.Caller.SendAsync("Error", new { message });

    private static object ToSource(RetrievalHit hit) => new
    {
        chunkId = hit.ChunkId,
        documentId = hit.DocumentId,
        fileName = hit.FileName,
        seq = hit.Seq,
        heading = hit.Heading,
        pageFrom = hit.PageFrom,
        pageTo = hit.PageTo,
        vectorScore = hit.VectorScore,
        textScore = hit.TextScore,
        fusedScore = hit.FusedScore,
        content = hit.Content
    };

    /// <summary>停止当前生成。</summary>
    public Task Stop()
    {
        if (Generations.TryGetValue(Context.ConnectionId, out var cts))
            cts.Cancel();
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Generations.TryRemove(Context.ConnectionId, out var cts))
            cts.Cancel();
        return base.OnDisconnectedAsync(exception);
    }
}

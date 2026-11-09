using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Knowledge.Local.Citations;
using AgentWorkbench.Knowledge.Local.Tools;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Auth;
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
    private readonly KnowledgeOptions _knowledgeOptions;
    private readonly IActionSubjectScope _subjectScope;
    private readonly ILogger<AgentHub> _logger;

    /// <summary>每个连接当前的生成任务，用于"停止生成"与断线取消。</summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Generations = new();

    public AgentHub(
        IAgentHarness harness,
        IConversationRepository conversations,
        IOptions<ProviderOptions> providerOptions,
        IOptions<KnowledgeOptions> knowledgeOptions,
        IActionSubjectScope subjectScope,
        ILogger<AgentHub> logger)
    {
        _harness = harness;
        _conversations = conversations;
        _providerOptions = providerOptions.Value;
        _knowledgeOptions = knowledgeOptions.Value;
        _subjectScope = subjectScope;
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

        // 连接也必须有身份：多租户下「谁在说话」不能靠客户端自报。
        if (Context.User.Identity?.IsAuthenticated != true)
        {
            await SendErrorAsync("未登录：请刷新页面重新登录。");
            return;
        }
        var (tenantId, userId) = Context.User.Owner();

        // 放开并行调用后 Stop 才能插队进来，代价是同连接可以同时发起两次 Send；
        // 这里用 TryAdd 挡掉第二条，否则后到的会覆盖前一条的 CTS，导致前一条再也停不掉。
        if (!Generations.TryAdd(Context.ConnectionId, linked))
        {
            await SendErrorAsync("已有生成在进行中，请先停止或等待完成。");
            return;
        }

        // 翻译工具在 harness 的工具环里执行，此刻的 HttpContext 不保证还在（长连接）。
        // 整段生成期间显式建立身份作用域，值随异步流进入工具调用；工具取不到身份会拒绝。
        using var subjectScope = _subjectScope.Begin(Context.User?.Identity?.Name);

        var answer = new StringBuilder();
        ConversationRecord? conversation = null;
        HarnessRunResult? run = null;

        // 本次作答真正用到的证据：工具输出那一刻的原始顺序，下标 +1 就是模型写的 [n]。
        // 只留最后一次非空检索——多次检索的编号各从 1 起算，合并必然撞号；
        // 模型最后看到的那批才是它写引用时手里有的编号。
        IReadOnlyList<RetrievalHit>? evidence = null;

        try
        {
            // 先探活：模型没配就当场告诉用户，不必白跑一次 run、白落一条失败轨迹。
            var capabilities = await _harness.GetCapabilitiesAsync(linked.Token);
            if (!capabilities.Llm)
            {
                await SendErrorAsync("模型未配置：请在「设置」页添加供应商与模型。");
                return;
            }

            // 客户端给的 conversationId 只是「我想接着哪个」，是否属于我由仓储判定：
            // 别人的 id 会新建一个新会话，既不越权也不泄露存在性。
            conversation = await _conversations.GetOrCreateAsync(request.ConversationId, tenantId, userId);
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
                        // 执行过程要看得见：每次工具调用完成就推一条，前端按序拼成时间线。
                        // 只推预览（截断），完整结果留在轨迹表里——这是给演示/排障看的，不是数据通道。
                        if (evt.Tool is { } tool)
                        {
                            await Clients.Caller.SendAsync("ReceiveTool", new
                            {
                                messageId,
                                iteration = tool.Iteration,
                                name = tool.Name,
                                arguments = Clip(tool.Arguments),
                                resultPreview = Clip(tool.Result),
                                error = tool.Error
                            }, linked.Token);
                        }

                        var hits = TryReadKnowledgeHits(evt.Tool);
                        if (hits is { Count: > 0 })
                        {
                            evidence = hits;
                            await Clients.Caller.SendAsync("ReceiveSources", new
                            {
                                messageId,
                                sources = hits.Select(ToSource).ToList()
                            }, linked.Token);
                        }

                        break;

                    case HarnessStreamEventKind.Failed:
                        // 失败已由 harness 落轨迹（含失败分类码），这里只负责让用户看见。
                        // 同时把终态发下去：没有它，「用户取消」和「服务出错」在界面上长得一样。
                        await NotifyStoppedAsync(messageId, evt.Status, evt.Error);
                        await SendErrorAsync(evt.Error ?? "生成失败。");
                        break;

                    case HarnessStreamEventKind.Completed:
                        // 终态由下面统一收尾（保存答复 + ReceiveDone），此处只接住结果。
                        run = evt.Result;
                        break;
                }
            }

            // 拒答是代码的结论，不是模型的输出：正文换成固定文案（此刻模型一个字都没产出），
            // 并且不跑引用校验——没有证据可引，跑一遍只会得到"全句无出处"，把拒答计进无出处句比例。
            var refused = string.Equals(run?.Code, AgentRunCode.KnowledgeInsufficient, StringComparison.Ordinal);
            if (refused)
            {
                answer.Clear().Append(run!.Text);
                await Clients.Caller.SendAsync("ReceiveRefusal", new
                {
                    messageId,
                    text = run.Text,
                    code = run.Code
                }, linked.Token);
            }

            await SaveAnswerAsync(conversation.Id, answer);
            if (!refused)
                await PushCitationsAsync(messageId, answer.ToString(), evidence, linked.Token);
            await Clients.Caller.SendAsync("ReceiveDone", new
            {
                messageId,
                conversationId = conversation.Id,
                // run 摘要：iterations / latencyMs / status / code 是「执行控制真的在生效」的唯一可视证据，
                // 没有它，界面上「答完了」和「跑满迭代被强制收口」长得一模一样。
                run = run is null ? null : new
                {
                    runId = run.RunId,
                    iterations = run.Iterations,
                    model = run.Model,
                    latencyMs = run.LatencyMs,
                    status = run.Status,
                    code = run.Code,
                    steps = run.Steps.Count,
                    tools = run.Steps.Count(s => string.Equals(s.Kind, "tool", StringComparison.Ordinal))
                }
            }, linked.Token);
        }
        catch (OperationCanceledException)
        {
            // 取消时 harness 的 Failed 事件常常送不进来（读端 token 已取消，await foreach 直接抛），
            // 所以终态要在这里补发一次，否则界面上的轨迹条永远停在「运行中」。
            await NotifyStoppedAsync(messageId, AgentRunStatus.Cancelled, "已停止生成。");
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
    /// 把 <c>search_knowledge</c> 的工具结果解析成命中列表；不是该工具或解析失败时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 返回原始 <see cref="RetrievalHit"/> 而不是投影后的匿名对象：同一份结果有两个消费者——
    /// 界面来源面板（<see cref="ToSource"/>）与引用校验（按同一顺序还原 <c>[n]</c>）。
    /// 来源面板是增益而非主链路：工具输出格式变了、被截断、或不是知识工具，都不该让整条对话挂掉。
    /// 空命中也不推面板——弹一个空的「来源」比不弹更让人困惑。
    /// </remarks>
    private IReadOnlyList<RetrievalHit>? TryReadKnowledgeHits(ToolCallTrace? trace)
    {
        if (trace is null
            || !string.Equals(trace.Name, SearchKnowledgeTool.ToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(trace.Result))
            return null;

        try
        {
            // 工具输出是 { hits, sufficiency } 两段：这里只取 hits，判定由否决层读，
            // 不在这条路径上重算——同一件事判两遍，迟早漂移。
            using var document = JsonDocument.Parse(trace.Result);
            if (!document.RootElement.TryGetProperty("hits", out var hitsElement))
                return null;

            var hits = hitsElement.Deserialize<List<RetrievalHit>>(SearchKnowledgeTool.SerializerOptions);
            return hits is null or [] ? null : hits;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "解析 search_knowledge 结果失败，本次不推送来源");
            return null;
        }
    }

    /// <summary>
    /// 推一次引用的体检报告：句级统计、逐条校验结论、剥掉越界标注后的正文。
    /// </summary>
    /// <remarks>
    /// 只在知识问答里推。普通对话没有证据集，所有标注都会被判越界——把一份"全无出处"的报告
    /// 推给一个本来就不查资料的对话，等于用错误的前提制造噪音。
    /// 推送失败不中断对话：报告是增益，正文已经发出去了。
    /// </remarks>
    private async Task PushCitationsAsync(
        string messageId,
        string answer,
        IReadOnlyList<RetrievalHit>? evidence,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = CitationVerifier.Verify(answer, evidence, _knowledgeOptions.CitationCoverageThreshold);

            // 第二层「承认」：这一整段有没有资料撑着。判定只在这里算一次，前端只读结论——
            // 两处各算一遍，迟早漂移，而漂移时两边都不报错。
            var support = CitationSupport.Judge(report, _knowledgeOptions.UnsupportedRatio);

            await Clients.Caller.SendAsync("ReceiveCitations", new
            {
                messageId,
                sentenceCount = report.SentenceCount,
                citedSentenceCount = report.CitedSentenceCount,
                uncitedSentenceCount = report.UncitedSentenceCount,
                outOfRangeCount = report.OutOfRangeCount,
                unsupportedCount = report.UnsupportedCount,
                // 注意这两个不是一回事：unsupportedCount 是"有几条引用指向了错块"（条），
                // supportVerdict 是"整段有没有资料撑"（段）。英文只差几个字母，含义完全不同。
                uncitedRatio = CitationSupport.UncitedRatio(report),
                supportVerdict = support.ToString().ToLowerInvariant(),
                cleanedAnswer = report.CleanedAnswer,
                citations = report.Citations.Select(c => new
                {
                    sentenceIndex = c.SentenceIndex,
                    evidenceNo = c.EvidenceNo,
                    fileName = c.FileName,
                    seq = c.Seq,
                    chunkId = c.ChunkId,
                    // 枚举转小写字符串：数字默认为 0/1/2，前端拿数字没法判断，也不可读。
                    check = c.Check.ToString().ToLowerInvariant(),
                    coverage = c.Coverage
                }).ToList()
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "推送引用校验结果失败：{MessageId}", messageId);
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

    /// <summary>工具参数/结果截断到可传输长度：完整内容在轨迹表里，界面上只需要够判断的量。</summary>
    private static string? Clip(string? text, int max = 600)
        => text is null || text.Length <= max ? text : text[..max] + "…";

    /// <summary>发终态通知；连接已断时静默失败（这里的目的只是让还活着的客户端知道发生了中止）。</summary>
    private async Task NotifyStoppedAsync(string messageId, string? status, string? error)
    {
        if (status is null) return;
        try
        {
            await Clients.Caller.SendAsync("ReceiveStopped", new { messageId, status, error });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "发送终态通知失败（连接可能已断开）");
        }
    }

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

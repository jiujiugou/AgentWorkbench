using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Knowledge.Local.Citations;
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
/// <param name="ConversationId">想接着哪个会话；不属于自己时由仓储另建新会话。</param>
/// <param name="Text">用户输入的自然语言。</param>
/// <param name="History">历史轮次（保留字段，当前由 ChatHistoryProvider 从会话存储恢复）。</param>
/// <param name="ClientMessageId">
/// 本次输入的客户端标识，由前端在点击发送时生成、在一次输入内保持不变。<b>必填</b>：
/// 它是派生动作请求号的输入之一，缺失时 Hub 直接拒绝、不起 run、不落消息。
/// 当前对话入口没有自动重发。<b>若引入重放</b>，除了复用同一次输入的值（服务端现编的号对重试
/// 无效），还必须先在本入口建输入级去重：键只能取认证主体 + 本值，此刻会话 id 尚未确定；
/// <c>action_idempotency</c> 只按 requestId 护住动作，护不住整段 run，缺了这层去重就会
/// 产生两条 user 消息、两次模型调用、两份轨迹。
/// </param>
public sealed record ChatRequest(
    string? ConversationId,
    string Text,
    List<ChatTurn>? History,
    string ClientMessageId);

/// <summary>
/// Agent 实时通道：接收用户消息，流式推送 token，并汇报工具调用事件。
/// 消息历史持久化到数据库，刷新/切换后可恢复。
/// </summary>
/// <remarks>
/// 只有一个入口 <see cref="Send"/>：说什么由模型判断该查资料、该查系统、该提动作还是该直答。
/// 曾经并列的第二条入口（<c>Ask</c>，知识问答）走的是<b>同一条 harness 链路</b>——同一 loop、
/// 同一执行控制、同一轨迹落库，差别只在注入的一句系统指令。它不改变工具集，所以那个「选择」是假的：
/// 选了「知识问答」照样能提动作，选了「通用对话」照样能检索。已删，指令改由
/// <c>KnowledgeInstructions.Entry</c> 按意图分派。
/// <para>
/// 此前这里绕过 harness 直连对话 / 检索服务，于是「用户实际在用的这条路」既没有轨迹也没有预算，
/// 评测一条都覆盖不到——这正是「验收假绿」的结构性来源。
/// 来源面板由工具完成事件驱动（模型调 MCP 检索工具时推送），不依赖服务端预检索。
/// </para>
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
    public Task Send(ChatRequest request) => RunAsync(request);

    /// <summary>
    /// 唯一入口的实现：建/取会话 → 起一次 harness 流 → 按事件推进前端。
    /// </summary>
    private async Task RunAsync(ChatRequest request)
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

        // 输入身份是判重的地基，必须在任何副作用之前判，且必须在占连接闸之前。
        // 缺了就静默退化成随机 requestId，同一句话重放会变成两张单——整套幂等白搭。
        // 放在 TryAdd 之前：判空返回时没有占用 Generations 槽位，不需要走 finally 清理。
        if (!InputIdentity.IsPresent(request.ClientMessageId))
        {
            await SendErrorAsync($"缺少本次输入的标识（{InputIdentity.MissingCode}），请刷新页面重试。");
            return;
        }

        // 空文本不能只靠前端拦：绕过前端直连 Hub 时，空串会一路走到建会话、
        // 落一条空 user 消息并起一次 run。放在占连接闸之前：拒绝时不占槽位、
        // 不需要 finally 清理，也不产生终态。
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            await SendErrorAsync("输入为空：请输入内容后再发送。");
            return;
        }

        // 放开并行调用后 Stop 才能插队进来，代价是同连接可以同时发起两次 Send；
        // 这里用 TryAdd 挡掉第二条，否则后到的会覆盖前一条的 CTS，导致前一条再也停不掉。
        //
        // 注意本闸的职责边界：它是<b>连接级防抖</b>，只保 Stop 能找到正确的 CTS。
        // 它<b>不</b>承担会话互斥——两个标签页是两条连接，本闸对它们完全无感；
        // 同一会话的并发由 Harness 的 ISessionRunGate 拦（返回 session_busy）。
        // 不要把这两层混成一层：删掉它 Stop 会坏，靠它挡并发则挡不住。
        if (!Generations.TryAdd(Context.ConnectionId, linked))
        {
            await SendErrorAsync("已有生成在进行中，请先停止或等待完成。");
            return;
        }

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

            // 翻译工具在 harness 的工具环里执行，此刻的 HttpContext 不保证还在（长连接）。
            // 整段生成期间显式建立作用域，值随异步流进入工具调用；工具取不到身份会拒绝。
            // 作用域在会话确定之后建立：conversationId 是派生请求号的输入，必须用真实会话 id，
            // 不能用客户端传来的那个（新会话时它为 null）。
            // clientMessageId 已在入口判空，这里放进去的是非空值——工具环里的
            // IActionOriginProvider 因而永远拿得到「这是哪一次输入」。
            using var subjectScope = _subjectScope.Begin(
                Context.User?.Identity?.Name, conversation.Id, request.ClientMessageId);

        // conversationId 同时就是 harness 的 sessionId：一个会话一份跨 run 记忆。
            // 消息正文另存对话表，专供界面恢复（会话快照是模型记忆，两者职责不同，不重复读）。
            var runRequest = new HarnessRunRequest(
                conversation.Id, request.Text, ResolveInstructions());

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
                        // Code 再细分一层：session_busy 表示这条会话正忙、本次根本没开始跑，前端应提示「稍候」。
                        await NotifyStoppedAsync(messageId, evt.Status, evt.Error, evt.Code);
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

            await SaveAnswerAsync(conversation.Id, answer, run?.RunId);
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
            await SaveAnswerAsync(conversation?.Id, answer, run?.RunId);
            await SendErrorAsync("已停止生成。");
        }
        catch (Exception ex)
        {
            await SaveAnswerAsync(conversation?.Id, answer, run?.RunId);
            _logger.LogError(ex, "生成失败");
            await SendErrorAsync(ex.Message);
        }
        finally
        {
            Generations.TryRemove(Context.ConnectionId, out _);
        }
    }

    /// <summary>
    /// 本次 run 的系统指令：按意图分派的通用规范放在前面，设置页里的自定义提示词接在后面。
    /// </summary>
    /// <remarks>
    /// 顺序是刻意的：规范先出现，模型先看到「哪些事不能编」；用户提示词属于个性化约束，
    /// 放在后面补足语气与风格。反过来（用户提示词在前）容易被读成「这些规矩可以覆盖」。
    /// </remarks>
    private string ResolveInstructions()
    {
        var prompt = _providerOptions.SystemPrompt;
        return string.IsNullOrWhiteSpace(prompt)
            ? KnowledgeInstructions.Entry
            : KnowledgeInstructions.Entry + "\n\n" + prompt;
    }

    /// <summary>MCP 检索工具全名：<c>mcp__{server}__{tool}</c>，与 appsettings 的 Mcp:Servers.Name 对应。</summary>
    private const string McpKnowledgeToolName = "mcp__rag__search_knowledge";

    /// <summary>MCP 工具结果的解析约定。camelCase + 大小写不敏感，与 Python server 的输出对齐。</summary>
    private static readonly JsonSerializerOptions McpHitOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 把 MCP 检索工具的结果解析成命中列表；不是该工具、无命中或解析失败时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 同一份结果有两个消费者：界面来源面板（<see cref="ToSource"/>）与引用校验
    /// （<c>CitationVerifier</c> 按同一顺序还原 <c>[n]</c>）。两处必须读同一份，
    /// 否则「编号对得上但内容对不上」这种最隐蔽的错会发生。
    /// <para>
    /// 兼容两种包装：MCP 工具结果可能是直接的对象，也可能是
    /// <c>{"content":[{"type":"text","text":"&lt;JSON&gt;"}]}</c>（SDK 版本差异）。
    /// 两种都取得到 <c>hits</c>；取不到就只记日志，不中断对话。
    /// </para>
    /// </remarks>
    private IReadOnlyList<RetrievalHit>? TryReadKnowledgeHits(ToolCallTrace? trace)
    {
        if (trace is null
            || !string.Equals(trace.Name, McpKnowledgeToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(trace.Result))
            return null;

        try
        {
            using var document = JsonDocument.Parse(trace.Result);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        using var inner = JsonDocument.Parse(text.GetString()!);
                        var hits = ReadHits(inner.RootElement);
                        if (hits is { Count: > 0 }) return hits;
                    }
                }
                return null;
            }

            return ReadHits(root);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "解析 MCP 检索结果失败，本次不推送来源");
            return null;
        }
    }

    private static List<RetrievalHit>? ReadHits(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hits", out var hitsElement))
            return null;
        var hits = hitsElement.Deserialize<List<RetrievalHit>>(McpHitOptions);
        return hits is null or [] ? null : hits;
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

    private async Task SaveAnswerAsync(string? conversationId, StringBuilder answer, long? runId)
    {
        if (string.IsNullOrEmpty(conversationId) || answer.Length == 0)
            return;
        try
        {
            // runId 落到消息上，界面上「回看历史」才能顺着它找回当时的执行过程。
            // 失败/取消轮可能还没拿到 run（HarnessRunResult 只在 Completed 时给出），此时为 null，
            // 界面按「没有过程」显示——不按时间戳猜归属。
            await _conversations.AddMessageAsync(conversationId, "assistant", answer.ToString(), runId);
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
    private async Task NotifyStoppedAsync(string messageId, string? status, string? error, string? code = null)
    {
        if (status is null) return;
        try
        {
            await Clients.Caller.SendAsync("ReceiveStopped", new { messageId, status, error, code });
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

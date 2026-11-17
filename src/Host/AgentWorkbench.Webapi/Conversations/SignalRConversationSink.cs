namespace AgentWorkbench.Webapi.Conversations;

using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Citations;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Tools;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IConversationSink"/> 的 SignalR 实现：<b>只做形状转换与推送，不做任何判定</b>。
/// </summary>
/// <remarks>
/// <para>
/// 这里出现的东西都是「传输细节」：<c>messageId</c>（前端拿它对流）、600 字截断
/// （界面只要够判断的量，全文在轨迹表）、字段名与枚举转小写（前端拿数字没法判断）。
/// 它们在编排层没有意义，所以全部收在这一层。
/// </para>
/// <para>
/// 持有 <see cref="ISingleClientProxy"/> 而不是整个 <c>Hub</c>：出口只需要「往这个调用者发」，
/// 不需要 <c>Context</c>、不需要 <c>Groups</c>。窄依赖换来可测性 —— 这一层可以脱离 Hub 单测。
/// </para>
/// </remarks>
public sealed class SignalRConversationSink : IConversationSink
{
    /// <summary>工具参数/结果的展示上限。完整内容在轨迹表里，界面只需要够判断的量。</summary>
    private const int ClipLimit = 600;

    /// <summary>只管「往发起这次调用的客户端发」，不碰 Hub 的 Context / Groups。</summary>
    private readonly ISingleClientProxy _caller;

    /// <summary>这一轮回答的 id（Hub 每轮新生成），前端靠它把四类流事件归到同一条消息。</summary>
    private readonly string _messageId;

    /// <summary>只用于「终态发送失败」那一条 Warning —— 连接已断是正常情况，不该吵到 LogError。</summary>
    private readonly ILogger _logger;

    /// <summary>会话 id 由 <see cref="ConversationStartedAsync"/> 交代；<c>ReceiveDone</c> 要带上它。</summary>
    private string? _conversationId;

    /// <param name="caller">只面向当前调用者的代理，窄依赖使本类可脱离 Hub 单测。</param>
    /// <param name="messageId">本轮回答 id，由 Hub 生成；本类只透传，不产生也不改变它。</param>
    /// <param name="logger">仅用于记录连接已断时的终态发送失败。</param>
    public SignalRConversationSink(ISingleClientProxy caller, string messageId, ILogger logger)
    {
        _caller = caller;
        _messageId = messageId;
        _logger = logger;
    }

    /// <summary>
    /// 推 <c>ReceiveConversation</c>，并把真实会话 id 记下来——新建会话时前端还不知道 id，
    /// 而后面的 <c>ReceiveDone</c> 必须带上它。
    /// </summary>
    /// <param name="conversationId">仓储返回的真实会话 id（新建时由后端生成）。</param>
    /// <param name="ct">调用方取消令牌。</param>
    public async Task ConversationStartedAsync(string conversationId, CancellationToken ct)
    {
        _conversationId = conversationId;
        await _caller.SendAsync("ReceiveConversation", new { conversationId }, ct);
    }

    /// <summary>
    /// 推 <c>ReceiveDelta</c>。正文一字不改——trim 在前端做，后端只判不修，
    /// 保证界面显示的和模型看到的始终是同一句话。
    /// </summary>
    public Task DeltaAsync(string text, CancellationToken ct)
        => _caller.SendAsync("ReceiveDelta", new { messageId = _messageId, delta = text }, ct);

    /// <summary>
    /// 推 <c>ReceiveTool</c>：参数和结果都过 <see cref="Clip"/> 只发预览，完整版留在轨迹表。
    /// error 原样带——工具失败要能在时间线上看得见。
    /// </summary>
    public Task ToolAsync(ToolCallTrace tool, CancellationToken ct)
        => _caller.SendAsync("ReceiveTool", new
        {
            messageId = _messageId,
            iteration = tool.Iteration,
            name = tool.Name,
            arguments = Clip(tool.Arguments),
            resultPreview = Clip(tool.Result),
            error = tool.Error
        }, ct);

    /// <summary>
    /// 推 <c>ReceiveSources</c>：<see cref="ToSource"/> 把命中转成来源面板的形状。
    /// 这里不校验引用、不算覆盖率——那些是判定的事，已在编排层算完。
    /// </summary>
    public Task SourcesAsync(IReadOnlyList<RetrievalHit> hits, CancellationToken ct)
        => _caller.SendAsync("ReceiveSources", new
        {
            messageId = _messageId,
            sources = hits.Select(ToSource).ToList()
        }, ct);

    /// <summary>
    /// 推 <c>ReceiveCitations</c>：把报告摊平成前端字段。判定值（supportVerdict / uncitedRatio）
    /// 由编排层算好传进来，这里绝不重算——两处各算一遍迟早漂移，而漂移时两边都不报错。
    /// </summary>
    public Task CitationsAsync(
        CitationReport report,
        string supportVerdict,
        double uncitedRatio,
        CancellationToken ct)
        => _caller.SendAsync("ReceiveCitations", new
        {
            messageId = _messageId,
            sentenceCount = report.SentenceCount,
            citedSentenceCount = report.CitedSentenceCount,
            uncitedSentenceCount = report.UncitedSentenceCount,
            outOfRangeCount = report.OutOfRangeCount,
            unsupportedCount = report.UnsupportedCount,
            // 注意这两个不是一回事：unsupportedCount 是「有几条引用指向了错块」（条），
            // supportVerdict 是「整段有没有资料撑」（段）。英文只差几个字母，含义完全不同。
            uncitedRatio,
            supportVerdict,
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
        }, ct);

    /// <summary>
    /// 推 <c>ReceiveRefusal</c>：拒答正文是代码给的固定文案，模型一个字没产出，
    /// 所以这里不推 Delta、也不跑引用校验。
    /// </summary>
    public Task RefusedAsync(string text, string? code, CancellationToken ct)
        => _caller.SendAsync("ReceiveRefusal", new { messageId = _messageId, text, code }, ct);

    /// <summary>
    /// 推 <c>ReceiveDone</c>。run 摘要（iterations / status / code / 工具数）是
    /// 「执行控制真的在生效」的唯一可视证据——没有它，「答完了」和「跑满迭代被强制收口」在界面上一样。
    /// conversationId 用上面记下的真实值，不用客户端传来的那个。
    /// </summary>
    public Task CompletedAsync(HarnessRunResult? run, CancellationToken ct)
        => _caller.SendAsync("ReceiveDone", new
        {
            messageId = _messageId,
            conversationId = _conversationId,
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
        }, ct);

    /// <summary>
    /// 推 <c>ReceiveStopped</c>。status 为 null 是当前实现约定的「没东西要说」，直接不发。
    /// 这是九个动词里唯一带静默 catch 的：连接已断时终态送不出去是正常情况，
    /// 记 Debug 即可，不能让它盖掉原始的运行结果。
    /// </summary>
    public async Task StoppedAsync(string? status, string? error, string? code, CancellationToken ct)
    {
        if (status is null) return;
        try
        {
            await _caller.SendAsync("ReceiveStopped", new { messageId = _messageId, status, error, code }, ct);
        }
        catch (Exception ex)
        {
            // 连接已断时静默：这里的目的只是让还活着的客户端知道发生了中止。
            _logger.LogDebug(ex, "发送终态通知失败（连接可能已断开）");
        }
    }

    /// <summary>
    /// 推 <c>Error</c>：入口拒绝 / 模型未配置 / 异常都走这里，是本地错误，不是 harness 的失败事件。
    /// 刻意不带 messageId —— 这类错误可能发生在会话与回答 id 都还没确立之前。
    /// </summary>
    public Task ErrorAsync(string message, CancellationToken ct)
        => _caller.SendAsync("Error", new { message }, ct);

    /// <summary>
    /// 截到展示长度并加省略号。null 原样透传（与空串区分），
    /// 截断只影响界面预览，不碰轨迹里的完整/截断语义。
    /// </summary>
    private static string? Clip(string? text, int max = ClipLimit)
        => text is null || text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 命中 → 来源面板的形状。字段名是前端契约，改这里等于改界面。
    /// </summary>
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
}

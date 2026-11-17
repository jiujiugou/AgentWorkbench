namespace AgentWorkbench.Webapi.Conversations;

using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Citations;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Tools;

/// <summary>
/// 一次对话的出口：<b>只说要发生什么，不关心怎么送出去</b>。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是「编排不该认识传输」。当前唯一的实现是 SignalR，但一次对话里真正与连接
/// 有关的只有「把事件送到客户端」这一件事；会话、落库、作用域、引用校验都与连接无关。
/// 把出口抽成一个接口之后，第二件事（加第二个入口）不必复制那段编排。
/// </para>
/// <para>
/// 刻意<b>不</b>出现在这里的东西：<c>messageId</c>（哪一次回答，由实现自己持）、
/// 截断长度（传输细节）、前端字段名（形状属于实现）。接口里只有动词。
/// </para>
/// <para>
/// 与 <c>IActionSubjectScope</c> 的分工：那个管「这次调用以谁的名义」，是<b>入</b>；
/// 这个管「结果给谁看」，是<b>出</b>。两者都不承载判定。
/// </para>
/// </remarks>
public interface IConversationSink
{
    /// <summary>会话已确定（可能是新建的）。之后的事件都属于它。</summary>
    Task ConversationStartedAsync(string conversationId, CancellationToken ct);

    /// <summary>正文增量。</summary>
    Task DeltaAsync(string text, CancellationToken ct);

    /// <summary>一次工具调用完成（参数与结果是完整版，截断由实现决定）。</summary>
    Task ToolAsync(ToolCallTrace tool, CancellationToken ct);

    /// <summary>本次作答用到的检索命中，顺序即 <c>[n]</c> 的编号。</summary>
    Task SourcesAsync(IReadOnlyList<RetrievalHit> hits, CancellationToken ct);

    /// <summary>引用体检报告。<paramref name="supportVerdict"/> 与 <paramref name="uncitedRatio"/> 已算好。</summary>
    Task CitationsAsync(
        CitationReport report,
        string supportVerdict,
        double uncitedRatio,
        CancellationToken ct);

    /// <summary>代码判定为拒答（模型未产出正文）。</summary>
    Task RefusedAsync(string text, string? code, CancellationToken ct);

    /// <summary>正常收尾。<paramref name="run"/> 为 null 表示没拿到运行结果。</summary>
    Task CompletedAsync(HarnessRunResult? run, CancellationToken ct);

    /// <summary>非正常收尾（取消 / harness 失败）。<paramref name="status"/> 为 null 时不发。</summary>
    Task StoppedAsync(string? status, string? error, string? code, CancellationToken ct);

    /// <summary>本地错误（入口拒绝、模型未配置、异常）。</summary>
    Task ErrorAsync(string message, CancellationToken ct);
}

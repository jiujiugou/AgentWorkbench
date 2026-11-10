namespace AgentWorkbench.Ports;

/// <summary>
/// 一条已发生动作的摘要（宿主追溯页用）。只读视图，不参与任何判定。
/// </summary>
/// <param name="RequestId">动作请求号（内部去重键，<b>不作为面向用户的主标识</b>）。</param>
/// <param name="ActionKey">业务动作名，取自轨迹的 <c>intent</c>（写入时即 <see cref="ActionRequest.ActionKey"/>）。</param>
/// <param name="At">这一单第一次落痕的时间，即提交时间。</param>
/// <param name="Status">终态；<c>null</c> 表示尚未收口（待审批 / 执行中 / 进程中断）。</param>
/// <param name="Code">终态归类码；回放时为 <c>replayed_from_idempotency</c>。</param>
/// <param name="ExternalId">外部系统单号；未执行或执行失败时为 <c>null</c>。</param>
public sealed record ActionSummary(
    string RequestId,
    string ActionKey,
    DateTimeOffset At,
    string? Status,
    string? Code,
    string? ExternalId);

/// <summary>
/// 动作历史的只读观察口。<b>与 <see cref="IAuditQuery"/> 同性质，都不启动任何东西</b>：
/// 没有它，追溯页只能靠人记住一个 <c>requestId</c> 才能查，等于没有入口。
///
/// <para>
/// 与 <c>IActionRunStore</c> 分开的理由：store 的语义是「结果存取」，而 <c>ActionResult</c>
/// 不含动作名与提交时间，拿不到列表要显示的列。这里的数据来源是<b>轨迹</b>
/// （<c>action_trace</c> 的 <c>intent</c> 就是 <c>ActionKey</c>），
/// 所以按「有轨迹即有动作」聚合，左连结果表取终态。
/// </para>
/// <para>
/// 发起人（<c>subject</c>）目前<b>不在轨迹里</b>，因此本视图不提供；只有审批人（<c>approver</c>）。
/// 这是 §4.1 第 5 句「谁」的一个已知缺口，不要在前端用其他字段替代。
/// </para>
/// </summary>
public interface IActionHistoryQuery
{
    /// <summary>最近的动作，按提交时间倒序。仅返回摘要，细节走 <see cref="IAuditQuery"/>。</summary>
    ValueTask<IReadOnlyList<ActionSummary>> RecentAsync(int limit, CancellationToken ct);
}

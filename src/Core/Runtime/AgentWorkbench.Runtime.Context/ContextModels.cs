using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Context;

/// <summary>一次装配的输入：本轮输入 + run 级指令。</summary>
/// <param name="Input">用户本轮输入原文。</param>
/// <param name="Instructions">run 级系统指令（人格 / 引用规范）；交给 <c>ChatOptions.Instructions</c>。</param>
/// <remarks>
/// 只有这两项。历史由 MAF 的 <c>ChatHistoryProvider</c> 托管，参考片段尚未接进真实 run——
/// 两者都不该出现在请求装配的输入里（见 docs/core/请求装配.md 第六节）。
/// </remarks>
public sealed record ContextRequest(string Input, string? Instructions = null);

/// <summary>一次装配的产物：交给 MAF 的两样东西，形态与框架一一对应。</summary>
/// <param name="Messages">对话消息。生产 run 只有本轮输入一条 <c>user</c>；历史由 <c>ChatHistoryProvider</c> 追加。</param>
/// <param name="Instructions">系统指令；无内容或停用时为 <c>null</c>。
/// 由调用方放进 <c>ChatOptions.Instructions</c>：MAF 每轮随请求下发，且不写进会话历史。</param>
public sealed record ContextPlan(
    IReadOnlyList<ChatMessage> Messages,
    string? Instructions);

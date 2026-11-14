using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Context;

/// <inheritdoc />
/// <remarks>
/// 只剩两件 MAF 不给的判定：本轮输入的消息形态、以及「停用 / 空指令时不注入」。
/// 指令形态交给 <c>ChatOptions.Instructions</c>，历史交给 <c>ChatHistoryProvider</c>，
/// 本类不包装任何 <c>IChatClient</c>，也不做片段预算——片段还没有任何生产调用方，
/// 等接进 run 时再加（见 docs/core/请求装配.md 第六节）。
/// </remarks>
internal sealed class RequestContextComposer(IOptions<ContextOptions> options, ChatHistoryProvider history)
    : IRequestContextComposer
{
    public ContextOptions Options => options.Value;

    public ContextPlan Compose(ContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("本轮输入不能为空。", nameof(request));

        // 本轮输入永远单独成一条 user 消息：历史由 MAF 的 ChatHistoryProvider 追加，
        // 组装层不把两者揉成一个字符串，否则回放与轨迹里分不清「模型看到的上一轮」和「用户这次说的话」。
        var messages = new[] { new ChatMessage(ChatRole.User, request.Input) };

        // 停用态或没有指令时不注入：不往 ChatOptions 里塞空串，也不空包一层客户端。
        var instructions = Options.Enabled && !string.IsNullOrWhiteSpace(request.Instructions)
            ? request.Instructions.Trim()
            : null;

        return new ContextPlan(messages, instructions);
    }

    /// <summary>
    /// 历史 provider 由 DI 提供，本模块不决定它存哪（内存 / SQLite）。
    /// 装配点在这里而不是 Harness：历史属于「上下文从哪来」，存储实现不属于 Context 的依赖。
    /// </summary>
    public ChatHistoryProvider CreateChatHistoryProvider() => history;
}

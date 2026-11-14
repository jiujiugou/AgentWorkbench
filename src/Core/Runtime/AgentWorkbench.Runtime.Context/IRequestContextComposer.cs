using Microsoft.Agents.AI;

namespace AgentWorkbench.Runtime.Context;

/// <summary>
/// 请求装配契约：把「本轮输入 + 指令」整理成 MAF 原生装法需要的两样东西。
/// </summary>
/// <remarks>
/// 本模块<b>不提供 <c>Wrap</c></b>。指令形态交给 MAF 的 <c>ChatOptions.Instructions</c>，
/// 历史交给 MAF 的 <c>ChatHistoryProvider</c>，消息用 MEA 的 <c>ChatMessage</c> 直接承载——
/// 这三样框架已有等价物，按「绝对不造轮子」不再自写装饰器。
/// <para>本模块剩下的只有框架没有等价物的两件判定：本轮输入的消息形态、停用 / 空指令时不注入。
/// 片段预算没有生产调用方，<b>不预留字段</b>；接进 run 那天再加。</para>
/// </remarks>
public interface IRequestContextComposer
{
    /// <summary>当前配置快照（供能力位与调用方判断是否启用）。</summary>
    ContextOptions Options { get; }

    /// <summary>按请求产出一次装配结果；停用态或无任何注入内容时 <see cref="ContextPlan.Instructions"/> 为 <c>null</c>。</summary>
    ContextPlan Compose(ContextRequest request);

    /// <summary>
    /// 把 DI 注入的历史 provider 转交给 <c>ChatClientAgent</c>。
    /// 本模块不决定它存哪（默认内存版，落库版由 <c>Memory.Persistence</c> 用 <c>Replace</c> 覆盖）。
    /// </summary>
    ChatHistoryProvider CreateChatHistoryProvider();
}

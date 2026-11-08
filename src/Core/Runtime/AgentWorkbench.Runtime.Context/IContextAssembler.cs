using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Context;

/// <summary>
/// 上下文组装契约：把「历史 + 本轮输入 + 指令 + 片段」组装成一次模型请求的上下文。
/// </summary>
/// <remarks>
/// 本模块只做组装，不读数据库、不调检索、不认识业务。历史上由各调用方各自拼消息
/// （Harness 只放本轮输入、ChatService 自己 BuildMessages、Workflow 自己拼大字符串），
/// 现在统一收在这里；业务语义（文档上下文、前置步骤）仍留在调用方，组装只负责消息形态与预算。
/// </remarks>
public interface IContextAssembler
{
    /// <summary>当前配置快照（供能力位与调用方判断是否启用）。</summary>
    ContextOptions Options { get; }

    /// <summary>按请求组装一次上下文。</summary>
    ContextBundle Assemble(ContextRequest request);

    /// <summary>
    /// 用一次组装结果装饰模型客户端：把指令与片段作为 system 消息注入到请求最前。
    /// 没有任何注入内容时原样返回 <paramref name="inner"/>。
    /// </summary>
    /// <remarks>
    /// 注入点是 <see cref="IChatClient"/> 装饰器，<b>不是</b> <c>ChatClientAgentOptions.AIContextProviders</c>。
    /// 后者会让 MEA 在工具执行环外面再包一层函数调用环，绕过守卫与轨迹采集
    /// （实测症状：同参重复调用不再判 loop_detected、工具异常不再归类 tool_error）。
    /// </remarks>
    IChatClient Wrap(IChatClient inner, ContextBundle bundle);

    /// <summary>创建本次 run 的会话历史提供者（MAF 的上下文能力）。</summary>
    ChatHistoryProvider CreateChatHistoryProvider();
}

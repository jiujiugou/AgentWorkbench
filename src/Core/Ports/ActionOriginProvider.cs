namespace AgentWorkbench.Ports;

/// <summary>
/// 调用来源端口：底座侧唯一允许取「这一次输入属于哪一句话」的入口。
///
/// 存在的理由只有一个——<b>幂等键需要一个稳定的父级</b>。
/// 去重表（<c>IActionRunStore.TryClaimIdempotencyAsync</c>）按 requestId 判重，
/// 而 requestId 若在工具里随机生成，同一个包裹送两次就会得到两个新号码，
/// 去重表永远命中不了。本端口提供派生 requestId 所需的两项输入，让「同一次输入」
/// 无论被触发几次，都收敛到同一个键。
///
/// 与 <see cref="IActionSubjectProvider"/> 同构：只有读取语义，怎么把值放进作用域是宿主的事。
/// <b>值只能来自宿主传输层，模型输出、工具参数、请求体一律不得提供</b>——
/// 否则模型可以自行决定「这次算不算重复」，等于把去重权交给模型。
///
/// 两项都取不到时（HTTP 手工递单、单元测试），派生逻辑回退为一次性 ID，
/// 行为与未引入本端口时一致。
/// </summary>
public interface IActionOriginProvider
{
    /// <summary>当前这次输入所属的会话标识；取不到时返回 null。</summary>
    string? ConversationId { get; }

    /// <summary>
    /// 当前这次输入的客户端消息标识，由入口生成、在一次输入内保持稳定。
    /// 取不到时返回 null，派生逻辑回退为一次性 ID。
    /// </summary>
    string? ClientMessageId { get; }
}

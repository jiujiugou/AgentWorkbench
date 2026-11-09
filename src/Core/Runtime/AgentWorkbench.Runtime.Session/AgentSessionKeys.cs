namespace AgentWorkbench.Runtime.Session;

/// <summary>
/// 会话状态袋（<c>AgentSession.StateBag</c>）里约定的键名。
///
/// 这些键是模块间的契约，不归任何一方私有：
/// <list type="bullet">
/// <item>编排层每次 run 前把 <see cref="SessionId"/> 写进 StateBag；</item>
/// <item>历史 provider 从同一个键读回它，据此查库。</item>
/// </list>
///
/// 为什么必须走 StateBag：MAF 的 <c>AgentSession</c> 本身不带 id（id 一直是调用方维护的键），
/// 而 provider 按官方要求是<b>跨会话复用</b>的单例，不能把当前会话 id 存进实例字段。
/// </summary>
public static class AgentSessionKeys
{
    /// <summary>当前会话的 id，与 <c>ISessionStore</c> 使用的键一致。</summary>
    public const string SessionId = "aw.session_id";
}

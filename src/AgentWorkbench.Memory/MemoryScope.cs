namespace AgentWorkbench.Memory;

/// <summary>记忆作用域类型取值（与 <c>memory_items.scope_type</c> 对应）。</summary>
public static class MemoryScopeTypes
{
    public const string Global = "global";
    public const string Agent = "agent";
    public const string User = "user";
    public const string Session = "session";
}

/// <summary>
/// 记忆作用域：<see cref="Type"/> 决定隔离维度，<see cref="Id"/> 决定同一维度内的实例。
/// 召回永远只发生在本作用域（外加可选的 <see cref="Global"/>）内，不跨作用域串数据。
/// </summary>
/// <remarks>
/// 带 id 的作用域是隐私边界：<see cref="Session"/> 的记忆只有同一个会话看得到。
/// 因此 scope 由编排层按当前 run 决定（见 <see cref="IMemoryScopeAccessor"/>），
/// 不接受模型参数直接指定别人的 id。
/// </remarks>
public sealed record MemoryScope(string Type, string Id)
{
    /// <summary>全局记忆：所有作用域都能召回到（<see cref="Id"/> 恒为空串）。</summary>
    public static readonly MemoryScope Global = new(MemoryScopeTypes.Global, string.Empty);

    public static MemoryScope Session(string sessionId) => new(MemoryScopeTypes.Session, sessionId);

    public static MemoryScope User(string userId) => new(MemoryScopeTypes.User, userId);

    public static MemoryScope Agent(string agentId) => new(MemoryScopeTypes.Agent, agentId);
}

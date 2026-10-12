using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentWorkbench.Memory;

/// <summary>
/// <see cref="ISessionFactory"/> 的默认实现：把 MAF 的 agent 能力收敛成「建 / 序列化 / 反序列化」。
/// 编排层每次 run 用当次 agent 构造一个即可。
/// </summary>
public sealed class AgentSessionFactory(AIAgent agent) : ISessionFactory
{
    public ValueTask<AgentSession> CreateAsync(CancellationToken cancellationToken)
        => agent.CreateSessionAsync(cancellationToken);

    /// <remarks>
    /// <c>JsonSerializerOptions</c> 必须为 <see langword="null"/>：MAF 的 source-generated 元数据在内部，
    /// 传自定义实例会让 Serialize / Deserialize 抛 <see cref="NotSupportedException"/>。
    /// 见 docs/跨模块记录/状态与记忆持久化方案.md §一。
    /// </remarks>
    public ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken)
        => agent.SerializeSessionAsync(session, null, cancellationToken);

    /// <inheritdoc cref="SerializeAsync"/>
    public ValueTask<AgentSession> DeserializeAsync(JsonElement state, CancellationToken cancellationToken)
        => agent.DeserializeSessionAsync(state, null, cancellationToken);
}

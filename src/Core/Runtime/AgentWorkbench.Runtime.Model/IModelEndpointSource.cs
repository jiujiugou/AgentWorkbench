namespace AgentWorkbench.Runtime.Model;

/// <summary>
/// 当前激活的模型供应商事实：地址、密钥，以及可用于回退的模型名。
/// </summary>
/// <remarks>
/// 这是<b>内核自己的词汇</b>，不是数据库记录：<c>ProviderRecord</c> / <c>ProviderModelRecord</c>
/// 属于 <c>AgentWorkbench.Domain</c>，一旦从这里返回，内核就又得编译期引用 Domain。
/// <see cref="ProviderId"/> 只是来源标识（供 <see cref="ChatEndpoint"/> 沿用），不参与任何判定。
/// </remarks>
public sealed record ActiveModelProvider(
    long? ProviderId,
    string BaseUrl,
    string ApiKey,
    string? DefaultModel,
    IReadOnlyList<string> Models);

/// <summary>
/// 注入口：底座问「数据库里现在激活的是哪家、有哪些模型」，但不理解供应商表怎么存、怎么查。
/// </summary>
/// <remarks>
/// 与 ⑤ 的 <c>ISessionStore</c>、⑥ 的 <c>IExecutionTraceWriter</c> 是同一个形状：
/// 接口在内核，实现在 <c>AgentWorkbench.Persistence</c>，方向是「实现 → 抽象」。
/// <para>
/// <b>只回答数据库侧的事实</b>：配置兜底、调用方指定的 <c>modelOverride</c>、
/// 以及「两者都没有 → 返回 null」的语义，全部留在 <see cref="ChatClientFactory"/>，
/// 因为 <c>Harness</c> 的 <c>model_unavailable</c> 归类与探活都依赖那个 null。
/// 把这个判断挪进实现，等于让存储层替内核决定状态语义。
/// </para>
/// </remarks>
public interface IModelEndpointSource
{
    /// <summary>取当前激活供应商；无激活供应商时返回 <c>null</c>（不是抛异常，也不是空对象）。</summary>
    Task<ActiveModelProvider?> GetActiveAsync(CancellationToken cancellationToken);
}

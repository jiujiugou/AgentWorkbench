namespace AgentWorkbench.Knowledge.Support;

/// <summary>模型可用性查询：只回答「现在能不能调模型」，不做任何生成。</summary>
/// <remarks>
/// 这里原本还有一条 <c>StreamAsync</c> —— v1 留下的第二条生成路径，自己做「拼 prompt → 调模型 →
/// 遇 4xx 降级重试」。v3 把生成统一收进 <c>IAgentHarness</c> 之后它没有任何调用方，已删除。
/// 生成路径只应有一条：再长出一条，工具环守卫、轨迹采集、输入身份就会各走一半。
/// </remarks>
public interface IChatService
{
    /// <summary>是否已配置可用模型（运行时解析，未配置时前端提示去设置页）。</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);
}

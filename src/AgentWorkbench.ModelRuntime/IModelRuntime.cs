namespace AgentWorkbench.ModelRuntime;

/// <summary>
/// 模型运行时：把供应商客户端包装成统一的调用语义（超时 / 重试 / 并发闸门 / 用量统计）。
/// 供应商差异由 Providers（Provider Adapter）消化，本模块只关心「一次调用怎么被约束和度量」。
/// </summary>
/// <remarks>
/// 这是本模块对外的两个扩展点：<see cref="Wrap"/> 是「装配入口」，<see cref="GetUsage"/> 是「用量出口」。
/// 调用方（Conversation / AgentHarness）先经 Providers 解析端点、拿到真实 <see cref="IChatClient"/>，
/// 再交给 <see cref="Wrap"/> 装饰。策略定义见 <see cref="ModelRuntimeOptions"/>，实现见 <see cref="ModelRuntimeService"/>。
/// </remarks>
public interface IModelRuntime
{
    /// <summary>用统一语义包装一个供应商客户端。可重复包装（幂等：已包装的客户端原样返回）。</summary>
    /// <param name="innerClient">Providers 产出的真实客户端。</param>
    /// <returns>
    /// 装饰后的客户端；若传入的已是本模块包装过的实例，则原样返回，避免重复叠加超时 / 重试 / 闸门。
    /// </returns>
    IChatClient Wrap(IChatClient innerClient);

    /// <summary>读取累计用量（供可观测模块 / 健康检查展示）。</summary>
    /// <returns>进程内累计的用量快照；读取为原子快照，可在任意线程随时调用。</returns>
    ModelUsageSnapshot GetUsage();
}

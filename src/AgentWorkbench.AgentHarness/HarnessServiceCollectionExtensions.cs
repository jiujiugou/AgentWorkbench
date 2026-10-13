namespace AgentWorkbench.AgentHarness;

/// <summary>
/// AgentHarness 模块：Agent 运行时编排门面 —— 把「模型运行时 + 工具目录 + 上下文 + 会话 + 执行环 + 轨迹」
/// 装配成一次可运行的 Agent 任务，自身不实现任何一项能力。
/// </summary>
public static class HarnessServiceCollectionExtensions
{
    /// <summary>
    /// 注册 AgentHarness 编排门面。无状态，故用单例；其依赖（模型运行时 / 工具 / 上下文 / 会话 / 执行环 /
    /// 轨迹 / 知识）由各自模块的 AddNitro* 注册，本方法不负责装配它们。
    /// </summary>
    public static IServiceCollection AddNitroAgentHarness(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentHarness, AgentHarnessService>();
        return services;
    }
}

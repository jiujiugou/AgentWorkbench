namespace AgentWorkbench.AgentHarness;

/// <summary>
/// AgentHarness 模块：Agent 运行时编排门面 —— 把「模型运行时 + 工具目录 + 上下文 + 会话 + 执行环 + 轨迹」
/// 装配成一次可运行的 Agent 任务，自身不实现任何一项能力。
/// </summary>
public static class HarnessServiceCollectionExtensions
{
    public static IServiceCollection AddNitroAgentHarness(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentHarness, AgentHarnessService>();
        return services;
    }
}

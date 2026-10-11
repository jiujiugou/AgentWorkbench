using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.AgentLoop;

/// <summary>AgentLoop 模块（执行控制）：迭代预算与 run 级超时策略。</summary>
public static class AgentLoopServiceCollectionExtensions
{
    public static IServiceCollection AddNitroAgentLoop(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentLoopOptions>()
            .Bind(configuration.GetSection(AgentLoopOptions.SectionName));
        services.AddSingleton<IAgentLoop, AgentLoopService>();
        return services;
    }
}

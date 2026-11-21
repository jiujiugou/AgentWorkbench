using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.Runtime.ExecutionControl;

/// <summary>执行控制模块：迭代预算与 run 级超时策略。</summary>
public static class AgentExecutionControlServiceCollectionExtensions
{
    public static IServiceCollection AddNitroExecutionControl(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentExecutionControlOptions>()
            .Bind(configuration.GetSection(AgentExecutionControlOptions.SectionName));
        services.AddSingleton<IAgentExecutionControl, AgentExecutionControlService>();
        return services;
    }
}

using AgentWorkbench.AgentHarness.Tools;

namespace AgentWorkbench.AgentHarness;

/// <summary>Agent Harness 模块（运行时横切轴）：loop / context / memory / tools / trace，基于 MAF。</summary>
public static class HarnessServiceCollectionExtensions
{
    public static IServiceCollection AddNitroAgentHarness(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HarnessOptions>().Bind(configuration.GetSection(HarnessOptions.SectionName));
        services.AddSingleton<IAgentToolProvider, BuiltinTools>();
        services.AddSingleton<IAgentHarness, AgentHarnessService>();
        return services;
    }
}

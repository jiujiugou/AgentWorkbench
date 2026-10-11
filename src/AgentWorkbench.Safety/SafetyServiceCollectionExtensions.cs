using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.Safety;

/// <summary>Safety 模块（可靠性与安全）：工具授权策略的注册入口。</summary>
public static class SafetyServiceCollectionExtensions
{
    public static IServiceCollection AddNitroSafety(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ToolPolicyOptions>()
            .Bind(configuration.GetSection(ToolPolicyOptions.SectionName));
        services.AddSingleton<IToolPolicy, ToolPolicy>();
        return services;
    }
}

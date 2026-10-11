using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.Context;

/// <summary>Context 模块（状态与记忆 / 上下文）：上下文组装与预算配置的注册入口。</summary>
public static class ContextServiceCollectionExtensions
{
    public static IServiceCollection AddNitroContext(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ContextOptions>()
            .Bind(configuration.GetSection(ContextOptions.SectionName));
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        return services;
    }
}

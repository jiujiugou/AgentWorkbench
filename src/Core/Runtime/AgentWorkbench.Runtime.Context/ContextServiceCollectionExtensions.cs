using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.Runtime.Context;

/// <summary>上下文模块注册：配置绑定 + 组装器单例。</summary>
public static class ContextServiceCollectionExtensions
{
    /// <summary>
    /// 注册上下文组装器。无状态，故用单例；它不读存储、不调检索，历史与片段由调用方传入。
    /// </summary>
    public static IServiceCollection AddNitroContext(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ContextOptions>()
            .Bind(configuration.GetSection(ContextOptions.SectionName));
        services.AddSingleton<IContextAssembler, ContextAssembler>();
        return services;
    }
}

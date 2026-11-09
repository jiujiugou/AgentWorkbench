using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentWorkbench.Runtime.Session;

/// <summary>Memory 模块（状态与记忆）：会话存储与记忆配置的注册入口。</summary>
public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddNitroMemory(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MemoryOptions>()
            .Bind(configuration.GetSection(MemoryOptions.SectionName));
        // 供 TTL / 淘汰判定使用；测试可注册假时间源覆盖（不覆盖已有注册）。
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISessionStore, SessionStore>();
        // 会话互斥：同一 sessionId 同时只允许一个 run。进程内实现，跨进程不成立（见 ISessionRunGate 注释）。
        services.AddSingleton<ISessionRunGate, SessionRunGate>();
        // 记忆作用域：由编排层在每次 run 设置，记忆工具读取；AsyncLocal 实现天然按 run 隔离。
        services.AddSingleton<IMemoryScopeAccessor, AsyncLocalMemoryScopeAccessor>();
        return services;
    }
}

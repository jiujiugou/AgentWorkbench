using AgentWorkbench.Observability;

namespace AgentWorkbench.Persistence;

/// <summary>
/// 轨迹落库的 DI 注册。**放在持久化层，不放在 Observability**：
/// 契约（<see cref="IExecutionTraceStore"/>）属于内核，选哪种存储属于应用侧；
/// 让契约模块去注册一个 EF Core 实现，就会把存储选型焊进内核的编译期依赖。
/// </summary>
public static class TraceStoreServiceCollectionExtensions
{
    /// <summary>注册执行轨迹的落库实现（`agent_runs` / `agent_steps`）。</summary>
    /// <remarks>
    /// 方法名沿用 <c>AddNitroObservability</c>，组合根调用点不用改；但它的家已经在持久化层。
    /// 要换存储（内存替身、OTel、别的库）时，替换点在调用方，不在内核。
    /// </remarks>
    public static IServiceCollection AddNitroObservability(this IServiceCollection services)
    {
        // 一个实例满足两种用途：内核注入写口，回放端点注入读口。
        // 分开注册是为了让「内核不读」体现在构造函数上，而不是靠注释约定。
        services.AddSingleton<ExecutionTraceStore>();
        services.AddSingleton<IExecutionTraceStore>(sp => sp.GetRequiredService<ExecutionTraceStore>());
        services.AddSingleton<IExecutionTraceWriter>(sp => sp.GetRequiredService<ExecutionTraceStore>());
        services.AddSingleton<IExecutionTraceReader>(sp => sp.GetRequiredService<ExecutionTraceStore>());
        return services;
    }
}

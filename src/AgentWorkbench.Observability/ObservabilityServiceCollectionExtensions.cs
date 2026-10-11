namespace AgentWorkbench.Observability;

/// <summary>Observability 模块（可观测与评估）：执行轨迹的落库与回放。</summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddNitroObservability(this IServiceCollection services)
    {
        services.AddSingleton<IExecutionTraceStore, ExecutionTraceStore>();
        return services;
    }
}

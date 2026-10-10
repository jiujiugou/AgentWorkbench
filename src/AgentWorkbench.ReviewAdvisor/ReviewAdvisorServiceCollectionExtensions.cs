using AgentWorkbench.ReviewAdvisor.Agent;

namespace AgentWorkbench.ReviewAdvisor;

/// <summary>ReviewAdvisor 模块（P3 审查 Agent · Agent Loop）：结论生成 + 轨迹 + 后台执行。</summary>
public static class ReviewAdvisorServiceCollectionExtensions
{
    public static IServiceCollection AddNitroReviewAdvisor(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAdvisorRunQueue, AdvisorRunQueue>();
        services.AddSingleton<ReviewAgentLoop>();
        services.AddSingleton<IReviewAdvisorService, ReviewAdvisorService>();
        services.AddHostedService<AdvisorWorker>();
        return services;
    }
}

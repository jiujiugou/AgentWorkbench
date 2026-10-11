using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.ModelRuntime;

/// <summary>ModelRuntime 模块（模型交互）：统一模型调用的超时 / 重试 / 并发 / 用量策略。</summary>
/// <remarks>
/// 模块职责见同目录 <c>README.md</c>：只负责「一次调用怎么被约束和度量」，
/// 端点选择归 Providers，run 级预算归 AgentLoop。
/// </remarks>
public static class ModelRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// 注册模型运行时：绑定 <see cref="ModelRuntimeOptions"/> 配置节，注册用量累计器与 <see cref="IModelRuntime"/>。
    /// </summary>
    /// <param name="services">服务集合（组合根调用）。</param>
    /// <param name="configuration">应用配置，用于绑定 <see cref="ModelRuntimeOptions.SectionName"/> 节。</param>
    /// <remarks>
    /// 全部注册为单例：策略参数需全进程一致，用量计数也需跨请求汇总；
    /// 工厂每次 <c>Wrap</c> 会 new 出独立的装饰客户端实例，但策略与计数仍共享这两个单例。
    /// </remarks>
    public static IServiceCollection AddNitroModelRuntime(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 配置节 Harness:ModelRuntime；缺省时用 ModelRuntimeOptions 的默认值（见该类型）。
        services.AddOptions<ModelRuntimeOptions>()
            .Bind(configuration.GetSection(ModelRuntimeOptions.SectionName));
        // 单例：请求数 / token 计数需在进程内跨调用累计。
        services.AddSingleton<ModelUsageMeter>();
        // 单例门面；其自身无状态，装饰逻辑在每次 Wrap 时创建。
        services.AddSingleton<IModelRuntime, ModelRuntimeService>();
        return services;
    }
}

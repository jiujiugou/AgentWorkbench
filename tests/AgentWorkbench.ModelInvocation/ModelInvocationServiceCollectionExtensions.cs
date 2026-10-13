using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.ModelInvocation;

/// <summary>ModelInvocation 模块（评测适配面）：单步模型调用的 DI 注册。</summary>
/// <remarks>
/// 命名沿用 src 模块的 <c>AddNitroXxx</c> 惯例，但它注册的服务只被
/// τ²-bench 适配端点（本项目的 Program.cs）消费，不进 Webapi 组合根。
/// </remarks>
public static class ModelInvocationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IModelInvoker"/>（单例：自身无状态，端点解析与客户端缓存由 Providers 单例负责）。
    /// </summary>
    /// <param name="services">服务集合（组合根调用）。</param>
    /// <param name="configuration">应用配置（当前实现不读自有配置节，保留参数与 AddNitroXxx 惯例对齐）。</param>
    public static IServiceCollection AddNitroModelInvocation(
        this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration; // 暂无自有配置节：策略参数（超时/重试/闸门）全部来自 ModelRuntime 的 Harness:ModelRuntime 节。
        services.AddSingleton<IModelInvoker, ModelInvokerService>();
        return services;
    }
}

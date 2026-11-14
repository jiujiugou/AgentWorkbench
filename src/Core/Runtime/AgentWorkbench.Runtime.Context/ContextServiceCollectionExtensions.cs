using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentWorkbench.Runtime.Context;

/// <summary>请求装配模块注册：配置绑定 + 装配器单例 + 历史 provider 默认实现。</summary>
public static class ContextServiceCollectionExtensions
{
    /// <summary>
    /// 注册请求装配器。无状态，故用单例；它不读存储、不调检索，历史由 MAF 的 provider 托管、片段由调用方传入。
    ///
    /// 同时注册<b>默认</b>的历史 provider（内存版）。它只是让没有存储模块的环境仍能跑起来；
    /// 落库版由 <c>AgentWorkbench.Memory.Persistence</c> 用 <c>Replace</c> 覆盖
    /// （与 <c>ISessionStore</c> 的覆盖方式一致，见 MemoryPersistenceServiceCollectionExtensions）。
    ///
    /// provider 注册为单例是官方要求：一个 provider 服务所有会话，session 相关状态只能放
    /// <c>AgentSession.StateBag</c>，不能放 provider 的实例字段。
    /// </summary>
    public static IServiceCollection AddNitroContext(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ContextOptions>()
            .Bind(configuration.GetSection(ContextOptions.SectionName));
        services.TryAddSingleton<ChatHistoryProvider>(
            _ => new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()));
        services.AddSingleton<IRequestContextComposer, RequestContextComposer>();
        return services;
    }
}

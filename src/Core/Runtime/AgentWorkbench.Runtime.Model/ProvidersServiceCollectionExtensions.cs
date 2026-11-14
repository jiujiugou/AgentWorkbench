using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Runtime.Model;

/// <summary>Providers 模块（模型网关）：多供应商/多模型运行时解析，输出 Microsoft.Extensions.AI 的 IChatClient。</summary>
/// <remarks>
/// 模块职责见同目录 <c>README.md</c>：只负责回答「用哪家、哪个模型」，并把解析结果变成一个 <c>IChatClient</c> 出口。
/// 一次调用的超时 / 重试 / 并发 / 用量归 ModelRuntime，流式推送给客户端归 Conversation。
/// </remarks>
public static class ProvidersServiceCollectionExtensions
{
    /// <summary>
    /// 注册模型网关：绑定 <see cref="ProviderOptions"/> 配置节、注册 <see cref="IChatClientFactory"/>。
    /// </summary>
    /// <param name="services">服务集合（组合根调用）。</param>
    /// <param name="configuration">应用配置，用于绑定 <see cref="ProviderOptions.SectionName"/> 节。</param>
    /// <remarks>
    /// 两点容易误读的地方：
    /// <list type="bullet">
    /// <item>这里注册的 <c>IHttpClientFactory</c> <b>不是</b>给模型调用用的 —— 模型调用走 OpenAI SDK 自带的 HTTP 栈；
    /// 它是给「拉取模型列表 / 连通性探测」这类普通 HTTP 请求准备的，消费者在 Webapi 的 SettingsController。</item>
    /// <item><see cref="IChatClientFactory"/> 注册为单例：工厂自身无状态，客户端缓存是进程内静态字段，
    /// 因此注册为 Scoped / Transient 也不会改变缓存行为，单例只是避免重复创建对象。</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddNitroProviders(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 配置节 Provider：BaseUrl / ApiKey / Model / Temperature / SupportsTools / SystemPrompt 等。
        // 它同时充当「数据库里没有任何供应商时」的回退来源（见 ChatClientFactory.ResolveAsync）。
        services.AddOptions<ProviderOptions>().Bind(configuration.GetSection(ProviderOptions.SectionName));
        services.AddHttpClient();
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        return services;
    }

    // 首次启动的种子（写库）不在这里：它属于数据库初始化，已移到持久化层的
    // ProviderSeedingExtensions。放在本模块会让内核重新编译期依赖存储，端口化就白做了。
}

using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Providers;

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

    /// <summary>首次运行：把环境变量 / appsettings 中的模型配置种子为一条激活供应商。</summary>
    /// <remarks>
    /// 幂等：只要数据库里已存在任意一条供应商，或配置尚未就绪（BaseUrl / ApiKey / Model 有缺），就直接返回，
    /// 因此可以放心在启动流程里无条件调用。种子的作用是让「首次启动」开箱即可对话；
    /// 之后用户可在「设置」页新增 / 切换供应商，配置节退居为兜底。
    /// </remarks>
    public static async Task SeedDefaultProviderAsync(this IServiceProvider services)
    {
        // 仓储是 Scoped 的，从根容器取服务必须自建 scope；using 同时界定了连接与作用域的释放边界。
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProviderOptions>>().Value;

        // 已有供应商 或 配置不完整 → 不种子（前者尊重用户已有数据，后者避免种出一条不可用的供应商）。
        if ((await repository.ListProvidersAsync()).Count != 0 || !options.IsConfigured)
            return;

        // 名称带「（来自配置）」后缀，便于用户辨认它是自动生成的、可以安全删除。
        var seed = await repository.CreateProviderAsync("默认（来自配置）", options.BaseUrl, options.ApiKey);
        await repository.ActivateProviderAsync(seed.Id);
        // 模型可选：配置里没写模型时只种供应商，模型名仍可走 ResolveAsync 的回退链补齐。
        if (!string.IsNullOrWhiteSpace(options.Model))
            await repository.AddModelAsync(seed.Id, options.Model, null);
    }
}

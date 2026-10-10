using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Providers;

/// <summary>Providers 模块（模型网关）：多供应商/多模型运行时解析，输出 Microsoft.Extensions.AI 的 IChatClient。</summary>
public static class ProvidersServiceCollectionExtensions
{
    public static IServiceCollection AddNitroProviders(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProviderOptions>().Bind(configuration.GetSection(ProviderOptions.SectionName));
        services.AddHttpClient();
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        return services;
    }

    /// <summary>首次运行：把环境变量 / appsettings 中的模型配置种子为一条激活供应商。</summary>
    public static async Task SeedDefaultProviderAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProviderOptions>>().Value;

        if ((await repository.ListProvidersAsync()).Count != 0 || !options.IsConfigured)
            return;

        var seed = await repository.CreateProviderAsync("默认（来自配置）", options.BaseUrl, options.ApiKey);
        await repository.ActivateProviderAsync(seed.Id);
        if (!string.IsNullOrWhiteSpace(options.Model))
            await repository.AddModelAsync(seed.Id, options.Model, null);
    }
}

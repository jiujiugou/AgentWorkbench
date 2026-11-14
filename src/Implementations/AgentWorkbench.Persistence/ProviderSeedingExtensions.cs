using AgentWorkbench.Shared.Options;

namespace AgentWorkbench.Persistence;

/// <summary>
/// 首次启动的供应商种子：把 appsettings / 环境变量里的模型配置写成一条激活供应商。
/// </summary>
/// <remarks>
/// 为什么在 Persistence 而不是 <c>Runtime.Model</c>：这是<b>写库</b>，不是内核能力。
/// 放在内核会让 <c>Runtime.Model</c> 重新编译期依赖 Persistence，端口化就白做了——
/// 那条边现在是「实现 → 抽象」，反过来就又成了「内核 → 存储」。
/// <para>
/// 幂等：已有任意一条供应商，或配置不完整（BaseUrl / ApiKey / Model 任一为空）时直接返回，
/// 因此可以在启动流程里无条件调用。
/// </para>
/// <para>
/// 顺序要求：<b>必须在迁移之后</b>调用，否则表还不存在。
/// </para>
/// </remarks>
public static class ProviderSeedingExtensions
{
    public static async Task SeedDefaultProviderAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProviderOptions>>().Value;

        // 已有供应商 → 尊重用户数据，不再种；配置不全 → 种出来也是不可用的一条，不如不种。
        if ((await repository.ListProvidersAsync()).Count != 0 || !options.IsConfigured)
            return;

        // 名字带「（来自配置）」后缀，便于用户辨认它可以安全删除。
        var seed = await repository.CreateProviderAsync("默认（来自配置）", options.BaseUrl, options.ApiKey);
        await repository.ActivateProviderAsync(seed.Id);

        // 模型可选：配置里没写模型时只种供应商，模型名仍可走 ResolveAsync 的回退链补齐。
        if (!string.IsNullOrWhiteSpace(options.Model))
            await repository.AddModelAsync(seed.Id, options.Model, null);
    }
}

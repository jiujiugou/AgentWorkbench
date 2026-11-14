namespace AgentWorkbench.Persistence;

/// <summary>
/// <see cref="IModelEndpointSource"/> 的 EF 实现：把「激活供应商 + 默认模型 + 模型列表」
/// 三次查询折叠成一个端口事实。
/// </summary>
/// <remarks>
/// 这个类存在的唯一理由是把「怎么查库」从内核里拿出去：<see cref="Runtime.Model.ChatClientFactory"/>
/// 现在只认识 <see cref="IModelEndpointSource"/>，编译期不再拖 EF / SQLite。
/// <para>
/// <b>不在这里做回退链</b>：配置兜底、调用方 <c>modelOverride</c>、「都没有 → null」的语义
/// 全留在内核。这几条决定 <c>Harness</c> 的 <c>model_unavailable</c> 归类与探活，
/// 挪进存储层等于让数据库替内核决定状态语义。
/// </para>
/// <para>
/// 仓储方法不带 <see cref="CancellationToken"/>，是既有契约；这里透传不了，如实标注不假装支持。
/// </para>
/// </remarks>
public sealed class EfModelEndpointSource : Runtime.Model.IModelEndpointSource
{
    private readonly IProviderRepository _providers;

    public EfModelEndpointSource(IProviderRepository providers) => _providers = providers;

    /// <inheritdoc />
    public async Task<Runtime.Model.ActiveModelProvider?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var active = await _providers.GetActiveProviderAsync();

        // 没有激活供应商、或激活的这条没填地址 → 视为「数据库侧没有可用端点」。
        // 返回 null 而不是空对象：调用方靠这个 null 判 model_unavailable，空对象会让它误以为配好了。
        if (active is null || string.IsNullOrWhiteSpace(active.BaseUrl))
            return null;

        var defaultModel = (await _providers.GetDefaultModelAsync(active.Id))?.Model;
        var models = (await _providers.ListModelsAsync(active.Id))
            .Select(m => m.Model)
            .ToList();

        return new Runtime.Model.ActiveModelProvider(
            active.Id, active.BaseUrl, active.ApiKey ?? string.Empty, defaultModel, models);
    }
}

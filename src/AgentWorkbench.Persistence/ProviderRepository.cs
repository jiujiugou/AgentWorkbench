using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

public interface IProviderRepository
{
    Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync();
    Task<ProviderRecord?> GetProviderAsync(long id);
    Task<ProviderRecord?> GetActiveProviderAsync();
    Task<ProviderRecord> CreateProviderAsync(string name, string baseUrl, string apiKey);
    Task<bool> UpdateProviderAsync(long id, string name, string baseUrl, string? apiKey);
    Task<bool> DeleteProviderAsync(long id);
    Task<bool> ActivateProviderAsync(long id);

    Task<IReadOnlyList<ProviderModelRecord>> ListModelsAsync(long providerId);
    Task<ProviderModelRecord> AddModelAsync(long providerId, string model, string? displayName);
    Task<bool> DeleteModelAsync(long id);
    Task<bool> SetDefaultModelAsync(long providerId, long modelId);
    Task<ProviderModelRecord?> GetDefaultModelAsync(long providerId);
}

/// <summary>模型供应商与其模型的仓储。</summary>
/// <remarks>
/// <para>EF Core 实现。这组仓储有一半方法不只是单条 SQL，而是「写入 + 维持不变量」：
/// 首个供应商自动激活、删掉激活项后顶上第一个、首个模型自动设为默认、删掉默认模型后顶上第一个。
/// 这些不变量在 EF 版里仍然由同一层维持，位置和顺序都没变。</para>
/// <para><b>为什么要显式事务</b>：<c>ActivateProviderAsync</c> 与 <c>SetDefaultModelAsync</c>
/// 都是「先把同组全部置 0，再把目标置 1」。两条语句之间没有事务时，并发会短暂出现零个激活项。</para>
/// </remarks>
public sealed class ProviderRepository(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<ProviderRepository> logger) : IProviderRepository
{
    public async Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Providers
            .AsNoTracking()
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.Id)
            .ToListAsync();
    }

    public async Task<ProviderRecord?> GetProviderAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<ProviderRecord?> GetActiveProviderAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Providers
            .AsNoTracking()
            .Where(p => p.IsActive == 1)
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<ProviderRecord> CreateProviderAsync(string name, string baseUrl, string apiKey)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");

        await using var db = await contextFactory.CreateDbContextAsync();
        var record = new ProviderRecord
        {
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            IsActive = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Providers.Add(record);
        await db.SaveChangesAsync();

        logger.LogInformation("Created provider {Name} ({Id})", name, record.Id);

        // 首个供应商自动激活
        if (await GetActiveProviderAsync() is null)
            await ActivateProviderAsync(record.Id);

        return (await GetProviderAsync(record.Id))!;
    }

    /// <remarks>
    /// 原 SQL 是 <c>api_key = COALESCE(@apiKey, api_key)</c>。EF 的 <c>ExecuteUpdate</c> 表达不了
    /// 「参数为 null 时保留原值」，所以这里退回读改写——多一次 SELECT，但语义完全一致：
    /// 传 <c>null</c> 表示「不改 key」，传空串才是「清空 key」。
    /// </remarks>
    public async Task<bool> UpdateProviderAsync(long id, string name, string baseUrl, string? apiKey)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var entity = await db.Providers.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is null)
        {
            logger.LogInformation("Update provider {Id}: not found", id);
            return false;
        }

        entity.Name = name;
        entity.BaseUrl = baseUrl;
        if (apiKey is not null)
            entity.ApiKey = apiKey;
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");

        await db.SaveChangesAsync();
        logger.LogInformation("Update provider {Id}: updated", id);
        return true;
    }

    public async Task<bool> DeleteProviderAsync(long id)
    {
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var removed = await db.Providers.Where(p => p.Id == id).ExecuteDeleteAsync();
            if (removed == 0)
            {
                logger.LogInformation("Delete provider {Id}: not found", id);
                return false;
            }
        }

        // 删除的是当前激活项时，把剩下的第一个顶上
        if (await GetActiveProviderAsync() is null)
        {
            var next = (await ListProvidersAsync()).FirstOrDefault();
            if (next is not null)
                await ActivateProviderAsync(next.Id);
        }

        logger.LogInformation("Delete provider {Id}: removed", id);
        return true;
    }

    public async Task<bool> ActivateProviderAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Providers.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, 0));

        var now = DateTimeOffset.UtcNow.ToString("o");
        var affected = await db.Providers
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, 1)
                .SetProperty(p => p.UpdatedAt, now));

        await transaction.CommitAsync();

        logger.LogInformation("Activate provider {Id}: {Activated}", id, affected > 0);
        return affected > 0;
    }

    public async Task<IReadOnlyList<ProviderModelRecord>> ListModelsAsync(long providerId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.ProviderModels
            .AsNoTracking()
            .Where(m => m.ProviderId == providerId)
            .OrderByDescending(m => m.IsDefault).ThenBy(m => m.Id)
            .ToListAsync();
    }

    public async Task<ProviderModelRecord> AddModelAsync(long providerId, string model, string? displayName)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var count = await db.ProviderModels.CountAsync(m => m.ProviderId == providerId);
        var record = new ProviderModelRecord
        {
            ProviderId = providerId,
            Model = model,
            DisplayName = displayName,
            IsDefault = count == 0 ? 1 : 0,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };

        db.ProviderModels.Add(record);
        await db.SaveChangesAsync();

        logger.LogInformation("Added model {Model} (id={Id}) for provider {ProviderId}", model, record.Id, providerId);
        return record;
    }

    /// <remarks>
    /// 删掉的是默认模型时把同供应商第一个顶上。这里刻意保留「先查后删再补」的三段式：
    /// 补齐动作依赖被删行的 <c>provider_id</c> 与 <c>is_default</c>，删完就拿不到了。
    /// </remarks>
    public async Task<bool> DeleteModelAsync(long id)
    {
        ProviderModelRecord? row;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            row = await db.ProviderModels.FirstOrDefaultAsync(m => m.Id == id);
            if (row is null)
            {
                logger.LogWarning("Delete model {Id}: not found", id);
                return false;
            }

            db.ProviderModels.Remove(row);
            await db.SaveChangesAsync();
        }

        if (row.IsDefault == 1)
        {
            var next = (await ListModelsAsync(row.ProviderId)).FirstOrDefault();
            if (next is not null)
                await SetDefaultModelAsync(row.ProviderId, next.Id);
        }

        logger.LogInformation("Delete model {Id}: removed", id);
        return true;
    }

    public async Task<bool> SetDefaultModelAsync(long providerId, long modelId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.ProviderModels
            .Where(m => m.ProviderId == providerId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDefault, 0));

        var affected = await db.ProviderModels
            .Where(m => m.Id == modelId && m.ProviderId == providerId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDefault, 1));

        await transaction.CommitAsync();

        logger.LogInformation("Set default model {ModelId} for provider {ProviderId}: {Updated}",
            modelId, providerId, affected > 0);
        return affected > 0;
    }

    public async Task<ProviderModelRecord?> GetDefaultModelAsync(long providerId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.ProviderModels
            .AsNoTracking()
            .Where(m => m.ProviderId == providerId)
            .OrderByDescending(m => m.IsDefault).ThenBy(m => m.Id)
            .FirstOrDefaultAsync();
    }
}

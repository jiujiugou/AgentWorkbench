using Dapper;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
public sealed class ProviderRepository : IProviderRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<ProviderRepository> _logger;

    public ProviderRepository(IDbConnectionFactory factory, ILogger<ProviderRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync()
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ProviderRecord>(
            "SELECT * FROM providers ORDER BY is_active DESC, id ASC");
        var list = rows.AsList();
        _logger.LogDebug("Listed {Count} providers in {ElapsedMs}ms", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task<ProviderRecord?> GetProviderAsync(long id)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var result = await connection.QuerySingleOrDefaultAsync<ProviderRecord>(
            "SELECT * FROM providers WHERE id = @id", new { id });
        _logger.LogDebug("Get provider {Id}: {Found} in {ElapsedMs}ms", id, result is not null, sw.ElapsedMilliseconds);
        return result;
    }

    public async Task<ProviderRecord?> GetActiveProviderAsync()
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var result = await connection.QuerySingleOrDefaultAsync<ProviderRecord>(
            "SELECT * FROM providers WHERE is_active = 1 ORDER BY id ASC LIMIT 1");
        _logger.LogDebug("Get active provider: {Found} in {ElapsedMs}ms", result is not null, sw.ElapsedMilliseconds);
        return result;
    }

    public async Task<ProviderRecord> CreateProviderAsync(string name, string baseUrl, string apiKey)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow.ToString("o");
        long id;
        using (var connection = _factory.Create())
        {
            id = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO providers (name, base_url, api_key, is_active, created_at, updated_at)
                VALUES (@Name, @BaseUrl, @ApiKey, 0, @Now, @Now);
                SELECT last_insert_rowid();
                """, new { Name = name, BaseUrl = baseUrl, ApiKey = apiKey, Now = now });
        }
        _logger.LogInformation("Created provider {Name} ({Id}) in {ElapsedMs}ms", name, id, sw.ElapsedMilliseconds);

        // 首个供应商自动激活
        if (await GetActiveProviderAsync() is null)
            await ActivateProviderAsync(id);

        return (await GetProviderAsync(id))!;
    }

    public async Task<bool> UpdateProviderAsync(long id, string name, string baseUrl, string? apiKey)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var affected = await connection.ExecuteAsync(
            """
            UPDATE providers
            SET name = @name,
                base_url = @baseUrl,
                api_key = COALESCE(@apiKey, api_key),
                updated_at = @updatedAt
            WHERE id = @id
            """, new { id, name, baseUrl, apiKey, updatedAt = DateTimeOffset.UtcNow.ToString("o") });
        var updated = affected > 0;
        _logger.LogInformation("Update provider {Id}: {Updated} in {ElapsedMs}ms", id, updated, sw.ElapsedMilliseconds);
        return updated;
    }

    public async Task<bool> DeleteProviderAsync(long id)
    {
        var sw = Stopwatch.StartNew();
        bool removed;
        using (var connection = _factory.Create())
        {
            removed = await connection.ExecuteAsync("DELETE FROM providers WHERE id = @id", new { id }) > 0;
        }

        if (removed)
        {
            // 删除的是当前激活项时，把剩下的第一个顶上
            if (await GetActiveProviderAsync() is null)
            {
                var next = (await ListProvidersAsync()).FirstOrDefault();
                if (next is not null)
                    await ActivateProviderAsync(next.Id);
            }
        }
        _logger.LogInformation("Delete provider {Id}: {Removed} in {ElapsedMs}ms", id, removed, sw.ElapsedMilliseconds);
        return removed;
    }

    public async Task<bool> ActivateProviderAsync(long id)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("UPDATE providers SET is_active = 0", transaction: transaction);
        var affected = await connection.ExecuteAsync(
            "UPDATE providers SET is_active = 1, updated_at = @now WHERE id = @id",
            new { id, now = DateTimeOffset.UtcNow.ToString("o") }, transaction);
        transaction.Commit();
        var activated = affected > 0;
        _logger.LogInformation("Activate provider {Id}: {Activated} in {ElapsedMs}ms", id, activated, sw.ElapsedMilliseconds);
        return activated;
    }

    public async Task<IReadOnlyList<ProviderModelRecord>> ListModelsAsync(long providerId)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ProviderModelRecord>(
            "SELECT * FROM provider_models WHERE provider_id = @providerId ORDER BY is_default DESC, id ASC",
            new { providerId });
        var list = rows.AsList();
        _logger.LogDebug("Listed {Count} models for provider {ProviderId} in {ElapsedMs}ms", list.Count, providerId, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task<ProviderModelRecord> AddModelAsync(long providerId, string model, string? displayName)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var connection = _factory.Create();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM provider_models WHERE provider_id = @providerId", new { providerId });
        var makeDefault = count == 0 ? 1 : 0;

        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO provider_models (provider_id, model, display_name, is_default, created_at)
            VALUES (@providerId, @model, @displayName, @makeDefault, @now);
            SELECT last_insert_rowid();
            """, new { providerId, model, displayName, makeDefault, now });

        var record = (await connection.QuerySingleAsync<ProviderModelRecord>(
            "SELECT * FROM provider_models WHERE id = @id", new { id }))!;
        _logger.LogInformation("Added model {Model} (id={Id}) for provider {ProviderId} in {ElapsedMs}ms", model, id, providerId, sw.ElapsedMilliseconds);
        return record;
    }

    public async Task<bool> DeleteModelAsync(long id)
    {
        var sw = Stopwatch.StartNew();
        ProviderModelRecord? row;
        using (var connection = _factory.Create())
        {
            row = await connection.QuerySingleOrDefaultAsync<ProviderModelRecord>(
                "SELECT * FROM provider_models WHERE id = @id", new { id });
            if (row is null)
            {
                _logger.LogWarning("Delete model {Id}: not found", id);
                return false;
            }
            await connection.ExecuteAsync("DELETE FROM provider_models WHERE id = @id", new { id });
        }

        // 删掉的是默认模型时，把同供应商第一个顶上
        if (row.IsDefault == 1)
        {
            var next = (await ListModelsAsync(row.ProviderId)).FirstOrDefault();
            if (next is not null)
                await SetDefaultModelAsync(row.ProviderId, next.Id);
        }
        _logger.LogInformation("Delete model {Id}: removed in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return true;
    }

    public async Task<bool> SetDefaultModelAsync(long providerId, long modelId)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            "UPDATE provider_models SET is_default = 0 WHERE provider_id = @providerId", new { providerId }, transaction);
        var affected = await connection.ExecuteAsync(
            "UPDATE provider_models SET is_default = 1 WHERE id = @modelId AND provider_id = @providerId",
            new { modelId, providerId }, transaction);
        transaction.Commit();
        var updated = affected > 0;
        _logger.LogInformation("Set default model {ModelId} for provider {ProviderId}: {Updated} in {ElapsedMs}ms", modelId, providerId, updated, sw.ElapsedMilliseconds);
        return updated;
    }

    public async Task<ProviderModelRecord?> GetDefaultModelAsync(long providerId)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var result = await connection.QuerySingleOrDefaultAsync<ProviderModelRecord>(
            "SELECT * FROM provider_models WHERE provider_id = @providerId ORDER BY is_default DESC, id ASC LIMIT 1",
            new { providerId });
        _logger.LogDebug("Get default model for provider {ProviderId}: {Found} in {ElapsedMs}ms", providerId, result is not null, sw.ElapsedMilliseconds);
        return result;
    }
}

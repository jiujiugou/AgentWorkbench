using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

/// <summary>租户目录的持久化。用户的增删改查全部交给 <c>UserManager&lt;AppUser&gt;</c>，这里只剩租户。</summary>
public interface ITenantRepository
{
    Task<IReadOnlyList<TenantRecord>> ListTenantsAsync();
    Task<TenantRecord?> GetTenantAsync(string id);
    Task<TenantRecord> CreateTenantAsync(string name);
}

/// <inheritdoc />
/// <remarks>
/// EF Core 实现。<c>DbContext</c> 由工厂创建：本类注册为 Singleton，而上下文是 Scoped。
/// </remarks>
public sealed class TenantRepository(IDbContextFactory<AppDbContext> contextFactory) : ITenantRepository
{
    public async Task<IReadOnlyList<TenantRecord>> ListTenantsAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<TenantRecord?> GetTenantAsync(string id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<TenantRecord> CreateTenantAsync(string name)
    {
        var record = new TenantRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };

        await using var db = await contextFactory.CreateDbContextAsync();
        db.Tenants.Add(record);
        await db.SaveChangesAsync();
        return record;
    }
}

using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

/// <summary>租户与用户的持久化。多租户的「谁」由这里提供。</summary>
public interface IIdentityRepository
{
    Task<int> CountUsersAsync();
    Task<IReadOnlyList<TenantRecord>> ListTenantsAsync();
    Task<TenantRecord?> GetTenantAsync(string id);
    Task<TenantRecord> CreateTenantAsync(string name);
    Task<UserRecord?> FindUserAsync(string userName);
    Task<UserRecord?> GetUserAsync(string id);
    Task<UserRecord> CreateUserAsync(string tenantId, string userName, string displayName, string passwordHash, string role = "member");
}

/// <inheritdoc />
/// <remarks>
/// EF Core 实现。<c>DbContext</c> 由工厂创建：本类注册为 Singleton，而上下文是 Scoped。
/// </remarks>
public sealed class IdentityRepository(IDbContextFactory<AppDbContext> contextFactory) : IIdentityRepository
{
    public async Task<int> CountUsersAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Users.CountAsync();
    }

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

    /// <remarks>
    /// 用户名大小写不敏感（原 SQL 显式 <c>COLLATE NOCASE</c>）。EF 的字符串比较会跟随列的
    /// COLLATE 定义，而 <c>users.user_name</c> 建表时没有声明 NOCASE，所以这里显式指定，
    /// 否则「Admin」与「admin」会被当成两个账号。
    /// </remarks>
    public async Task<UserRecord?> FindUserAsync(string userName)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => EF.Functions.Collate(u.UserName, "NOCASE") == userName);
    }

    public async Task<UserRecord?> GetUserAsync(string id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<UserRecord> CreateUserAsync(
        string tenantId, string userName, string displayName, string passwordHash, string role = "member")
    {
        var record = new UserRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            UserName = userName.Trim(),
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            Role = role,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };

        await using var db = await contextFactory.CreateDbContextAsync();
        db.Users.Add(record);
        await db.SaveChangesAsync();
        return record;
    }
}

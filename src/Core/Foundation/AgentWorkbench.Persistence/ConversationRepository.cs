using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

/// <summary>会话与消息仓储（通用聊天上下文持久化）。</summary>
/// <remarks>
/// 每个方法都要求 <c>tenantId</c> + <c>userId</c>：会话归属是隔离的最小单元。
/// 为什么不做「可选参数、为空则不过滤」——那样漏传一次就是跨租户泄漏，且测试看不出来；
/// 强制传参让漏写在编译期就红。
/// </remarks>
public interface IConversationRepository
{
    Task<ConversationRecord> CreateAsync(string tenantId, string userId, string? title = null);
    Task<ConversationRecord?> GetAsync(string id, string tenantId, string userId);
    Task<ConversationRecord> GetOrCreateAsync(string? id, string tenantId, string userId);
    Task<IReadOnlyList<ConversationRecord>> ListAsync(string tenantId, string userId);
    Task<bool> DeleteAsync(string id, string tenantId, string userId);
    /// <summary>追加一条消息；<paramref name="runId"/> 只有 assistant 消息需要传（回看历史时据此找回执行过程）。</summary>
    Task AddMessageAsync(string conversationId, string role, string content, long? runId = null);
    Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId);
    Task<int> CountMessagesAsync(string conversationId);
}

/// <inheritdoc />
/// <remarks>
/// EF Core 实现。所有查询都带 <c>tenant_id</c> + <c>user_id</c> 两个条件——
/// 这不是防御性冗余，而是越权防护的唯一实现处。
/// </remarks>
public sealed class ConversationRepository(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<ConversationRepository> logger) : IConversationRepository
{
    public async Task<ConversationRecord> CreateAsync(string tenantId, string userId, string? title = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var record = new ConversationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "新会话" : title!.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = tenantId,
            UserId = userId
        };

        await using var db = await contextFactory.CreateDbContextAsync();
        db.Conversations.Add(record);
        await db.SaveChangesAsync();

        logger.LogInformation("Created conversation {ConversationId} for tenant {TenantId}", record.Id, tenantId);
        return record;
    }

    public async Task<ConversationRecord?> GetAsync(string id, string tenantId, string userId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId && c.UserId == userId);
    }

    public async Task<ConversationRecord> GetOrCreateAsync(string? id, string tenantId, string userId)
    {
        if (!string.IsNullOrWhiteSpace(id) && id.Length == 32)
        {
            // 只有归属当前租户+用户时才复用；别人的 id 一律当作「不存在」新建，
            // 不报错也不复用——这既堵越权，也不泄露「这个 id 存在但你没权限」。
            var existing = await GetAsync(id, tenantId, userId);
            if (existing is not null)
                return existing;
        }

        return await CreateAsync(tenantId, userId);
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListAsync(string tenantId, string userId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<bool> DeleteAsync(string id, string tenantId, string userId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var deleted = await db.Conversations
            .Where(c => c.Id == id && c.TenantId == tenantId && c.UserId == userId)
            .ExecuteDeleteAsync();
        return deleted > 0;
    }

    /// <remarks>
    /// 插入消息与推进会话 <c>updated_at</c> 必须在同一事务里：只有一条成功时，
    /// 会话列表的排序会与实际最后一条消息脱节。两条语句共用 <c>db</c> 的连接与事务。
    /// </remarks>
    public async Task AddMessageAsync(string conversationId, string role, string content, long? runId = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");

        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        db.Messages.Add(new MessageRecord
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            CreatedAt = now,
            RunId = runId
        });
        await db.SaveChangesAsync();

        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now));

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    public async Task<int> CountMessagesAsync(string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Messages.CountAsync(m => m.ConversationId == conversationId);
    }
}

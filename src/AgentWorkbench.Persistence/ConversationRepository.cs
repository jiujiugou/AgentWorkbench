using Dapper;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Persistence;

/// <summary>会话与消息仓储（通用聊天上下文持久化）。</summary>
public interface IConversationRepository
{
    Task<ConversationRecord> CreateAsync(string? title = null);
    Task<ConversationRecord?> GetAsync(string id);
    Task<ConversationRecord> GetOrCreateAsync(string? id);
    Task<IReadOnlyList<ConversationRecord>> ListAsync();
    Task<bool> DeleteAsync(string id);
    Task AddMessageAsync(string conversationId, string role, string content);
    Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId);
    Task<int> CountMessagesAsync(string conversationId);
}

/// <inheritdoc />
public sealed class ConversationRepository : IConversationRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<ConversationRepository> _logger;

    public ConversationRepository(IDbConnectionFactory factory, ILogger<ConversationRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<ConversationRecord> CreateAsync(string? title = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var record = new ConversationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "新会话" : title!.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            "INSERT INTO conversations (id, title, created_at, updated_at) VALUES (@Id, @Title, @CreatedAt, @UpdatedAt)",
            record);
        _logger.LogInformation("Created conversation {ConversationId}", record.Id);
        return record;
    }

    public async Task<ConversationRecord?> GetAsync(string id)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<ConversationRecord>(
            "SELECT * FROM conversations WHERE id = @id", new { id });
    }

    public async Task<ConversationRecord> GetOrCreateAsync(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id.Length == 32)
        {
            var existing = await GetAsync(id);
            if (existing is not null)
                return existing;
        }

        return await CreateAsync();
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListAsync()
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ConversationRecord>(
            "SELECT * FROM conversations ORDER BY updated_at DESC");
        return rows.AsList();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var connection = _factory.Create();
        var deleted = await connection.ExecuteAsync("DELETE FROM conversations WHERE id = @id", new { id });
        return deleted > 0;
    }

    public async Task AddMessageAsync(string conversationId, string role, string content)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "INSERT INTO messages (conversation_id, role, content, created_at) VALUES (@ConversationId, @Role, @Content, @CreatedAt)",
            new { ConversationId = conversationId, Role = role, Content = content, CreatedAt = now },
            transaction);
        await connection.ExecuteAsync(
            "UPDATE conversations SET updated_at = @now WHERE id = @conversationId",
            new { now, conversationId }, transaction);

        transaction.Commit();
    }

    public async Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<MessageRecord>(
            "SELECT * FROM messages WHERE conversation_id = @conversationId ORDER BY id", new { conversationId });
        return rows.AsList();
    }

    public async Task<int> CountMessagesAsync(string conversationId)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM messages WHERE conversation_id = @conversationId", new { conversationId });
    }
}

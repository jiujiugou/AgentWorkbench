using System.Globalization;
using System.Text.Json;
using AgentWorkbench.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Memory.Persistence.ChatHistory;

/// <summary>会话历史的逐条存取。只做「追加」与「按序读回」，没有更新、没有删除。</summary>
public interface IChatMessageStore
{
    /// <summary>按写入顺序读回整个会话的历史；没有记录时返回空。</summary>
    Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>把一批消息追加到会话末尾。空批次是 no-op。</summary>
    Task AppendAsync(
        string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>走 EF Core（<see cref="IDbContextFactory{TContext}"/>），与 `ConversationRepository` 等仓储同一套。
/// 建表仍由 FluentMigrator 负责，本类不迁移、不 `EnsureCreated`。</para>
///
/// <para>单条消息用 <see cref="AIJsonUtilities.DefaultOptions"/> 序列化：它带 M.E.AI 的类型判别信息，
/// functionCall / functionResult 不会退化成纯文本（见 HistoryProviderProbeTests.H2）。</para>
///
/// <para>读取一律 <c>OrderBy(Id)</c>：Id 是 SQLite 自增，会话内即写入顺序。
/// 不用 CreatedAt 排序——同一批消息的时间戳相同，排序会退化成任意顺序。</para>
///
/// <para>反序列化失败直接抛：坏行说明写入侧出了问题，静默跳过会让模型带着残缺历史继续跑，
/// 那比整轮失败更难排查。</para>
/// </remarks>
public sealed class SqliteChatMessageStore(IDbContextFactory<AppDbContext> contexts) : IChatMessageStore
{
    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var payloads = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .Select(m => m.Payload)
            .ToListAsync(cancellationToken);

        var messages = new List<ChatMessage>(payloads.Count);
        foreach (var payload in payloads)
        {
            var message = JsonSerializer.Deserialize<ChatMessage>(payload, AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidOperationException($"会话 {sessionId} 的历史里有无法解析的消息行");
            messages.Add(message);
        }

        return messages;
    }

    public async Task AppendAsync(
        string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.ChatMessages.AddRange(messages.Select(message => new ChatMessageRecord
        {
            SessionId = sessionId,
            Role = message.Role.Value,
            Payload = JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions),
            CreatedAt = now
        }));

        // 一次 SaveChanges = 一个事务：要么这批全进，要么一条都不进。
        await db.SaveChangesAsync(cancellationToken);
    }
}

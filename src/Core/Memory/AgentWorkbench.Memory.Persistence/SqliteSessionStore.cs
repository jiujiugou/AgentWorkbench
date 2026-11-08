using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>
/// 落库版会话存储：把会话序列化后写进 SQLite，进程重启后同一 <c>sessionId</c> 仍能续上。
/// 过期会话按未命中处理（删行 + 新建），因此长期不用不会在表里堆积。
/// </summary>
public sealed class SqliteSessionStore : ISessionStore
{
    private readonly ISessionStateRepository _repository;
    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SqliteSessionStore> _logger;

    public SqliteSessionStore(
        ISessionStateRepository repository,
        IOptions<MemoryOptions> options,
        TimeProvider timeProvider,
        ILogger<SqliteSessionStore> logger)
    {
        _repository = repository;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    public async Task<AgentSession> GetOrCreateAsync(
        string sessionId,
        ISessionFactory factory,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(sessionId, cancellationToken);
        if (record is not null)
        {
            if (IsExpired(record.ExpiresAt))
            {
                await _repository.DeleteAsync(sessionId, cancellationToken);
                _logger.LogInformation("会话已过期，按新会话处理 session={SessionId}", sessionId);
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(record.StateJson);
                    // Clone：JsonDocument 在这里被释放，反序列化方拿到的元素必须独立于文档。
                    return await factory.DeserializeAsync(document.RootElement.Clone(), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 快照损坏或 MAF 版本不兼容：不能让会话永久卡死，丢掉重来。
                    _logger.LogWarning(ex, "会话快照无法反序列化，按新会话处理 session={SessionId}", sessionId);
                    await _repository.DeleteAsync(sessionId, cancellationToken);
                }
            }
        }

        return await factory.CreateAsync(cancellationToken);
    }

    public async Task SaveAsync(
        string sessionId,
        ISessionFactory factory,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var state = await factory.SerializeAsync(session, cancellationToken);
        var now = _time.GetUtcNow();
        var existing = await _repository.GetAsync(sessionId, cancellationToken);

        await _repository.UpsertAsync(new SessionStateRecord
        {
            SessionId = sessionId,
            StateJson = state.GetRawText(),
            CreatedAt = existing?.CreatedAt ?? Format(now),
            UpdatedAt = Format(now),
            ExpiresAt = _options.SessionTtlMinutes > 0
                ? Format(now.AddMinutes(_options.SessionTtlMinutes))
                : null
        }, cancellationToken);
    }

    private bool IsExpired(string? expiresAt)
        => expiresAt is not null
           && DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
               out var parsed)
           && parsed <= _time.GetUtcNow();

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}

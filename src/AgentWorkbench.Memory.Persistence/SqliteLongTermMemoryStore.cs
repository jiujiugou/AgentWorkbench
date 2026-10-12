using System.Globalization;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>
/// 长期记忆的 SQLite 实现：所有作用域/去重/保留期语义都在这里，表结构由 <see cref="IMemoryItemRepository"/> 负责。
/// </summary>
/// <remarks>
/// 只回结构化 <see cref="MemoryItem"/>，不返回 prompt 文本，也不决定注入几条（那是 <c>Context</c> 的职责）。
/// </remarks>
public sealed class SqliteLongTermMemoryStore : ILongTermMemoryStore
{
    private readonly IMemoryItemRepository _repository;
    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;

    public SqliteLongTermMemoryStore(
        IMemoryItemRepository repository, IOptions<MemoryOptions> options, TimeProvider timeProvider)
    {
        _repository = repository;
        _options = options.Value;
        _time = timeProvider;
    }

    public async Task<MemoryItem> RememberAsync(MemoryWrite write, CancellationToken cancellationToken = default)
    {
        var content = (write.Content ?? string.Empty).Trim();
        if (content.Length == 0)
            throw new ArgumentException("记忆内容不能为空。", nameof(write));

        if (_options.MaxMemoryChars > 0 && content.Length > _options.MaxMemoryChars)
            throw new ArgumentException($"记忆内容超过 {_options.MaxMemoryChars} 字符上限。", nameof(write));

        ValidateScope(write.Scope);

        var now = _time.GetUtcNow();
        var key = string.IsNullOrWhiteSpace(write.Key) ? null : write.Key.Trim();

        // 有 key 时先取原行：UPSERT 只覆盖内容类列，id / created_at / use_count 要沿用，
        // 这样「覆盖」不会把一条用了很多次的记忆重置成新记忆。
        var existing = key is null
            ? null
            : await _repository.GetByKeyAsync(write.Scope.Type, write.Scope.Id, key, cancellationToken);

        var record = new MemoryItemRecord
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            ScopeType = write.Scope.Type,
            ScopeId = write.Scope.Id,
            Kind = MemoryKinds.Normalize(write.Kind),
            MemKey = key,
            Content = content,
            Importance = Clamp(write.Importance),
            SourceSessionId = write.SourceSessionId,
            SourceRunId = write.SourceRunId,
            CreatedAt = existing?.CreatedAt ?? Iso(now),
            UpdatedAt = Iso(now),
            LastUsedAt = existing?.LastUsedAt,
            UseCount = existing?.UseCount ?? 0,
            ExpiresAt = IsoOrNull(RetentionOf(write), now)
        };

        return ToItem(await _repository.UpsertAsync(record, cancellationToken));
    }

    public async Task<IReadOnlyList<MemoryItem>> RecallAsync(
        MemoryQuery query, CancellationToken cancellationToken = default)
    {
        ValidateScope(query.Scope);

        var now = _time.GetUtcNow();
        var limit = query.CandidateK > 0 ? query.CandidateK : _options.RecallCandidateK;

        var rows = await _repository.QueryAsync(
            new MemoryItemQuery(
                query.Scope.Type, query.Scope.Id, query.IncludeGlobal, query.Text, query.Kinds, limit, Iso(now)),
            cancellationToken);

        if (rows.Count == 0)
            return [];

        // 使用统计是「顺带」信息：写失败不该让召回失败（否则观测动作会变成可用性单点）。
        // 同样地，返回对象直接按「本次命中」补上统计值，不依赖写回结果。
        await _repository.TouchAsync(rows.Select(row => row.Id).ToList(), Iso(now), cancellationToken);

        return rows
            .Select(row => ToItem(row) with { UseCount = row.UseCount + 1, LastUsedAt = now })
            .ToList();
    }

    public Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default)
        => _repository.DeleteAsync(id, cancellationToken);

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        => _repository.DeleteExpiredAsync(Iso(_time.GetUtcNow()), cancellationToken);

    private TimeSpan? RetentionOf(MemoryWrite write)
    {
        if (write.Retention is { } explicitRetention)
            return explicitRetention <= TimeSpan.Zero ? null : explicitRetention;

        return _options.LongTermRetentionDays > 0
            ? TimeSpan.FromDays(_options.LongTermRetentionDays)
            : null;
    }

    private static void ValidateScope(MemoryScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.Type))
            throw new ArgumentException("记忆作用域类型不能为空。", nameof(scope));

        // global 的 id 恒为空；其余作用域必须有 id，否则「隔离」退化成「谁都能写同一条」。
        if (scope.Type != MemoryScopeTypes.Global && string.IsNullOrWhiteSpace(scope.Id))
            throw new ArgumentException($"作用域 '{scope.Type}' 必须带 id。", nameof(scope));
    }

    private static double Clamp(double importance)
        => double.IsNaN(importance) ? 0.5 : Math.Clamp(importance, 0d, 1d);

    private static string Iso(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string? IsoOrNull(TimeSpan? retention, DateTimeOffset now)
        => retention is { } span ? Iso(now.Add(span)) : null;

    private static MemoryItem ToItem(MemoryItemRecord record) => new(
        record.Id,
        new MemoryScope(record.ScopeType, record.ScopeId),
        record.Kind,
        record.MemKey,
        record.Content,
        record.Importance,
        record.SourceSessionId,
        record.SourceRunId,
        Parse(record.CreatedAt) ?? default,
        Parse(record.UpdatedAt) ?? default,
        Parse(record.LastUsedAt),
        record.UseCount,
        Parse(record.ExpiresAt));

    private static DateTimeOffset? Parse(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

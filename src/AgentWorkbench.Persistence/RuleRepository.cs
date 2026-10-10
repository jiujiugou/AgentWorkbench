using Dapper;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentWorkbench.Persistence;

public interface IRuleRepository
{
    Task<IReadOnlyList<RuleRecord>> ListAsync();
    Task<IReadOnlyList<RuleRecord>> ListEnabledAsync();
    Task<RuleRecord> InsertAsync(string category, string name, string body);
    Task<RuleRecord> InsertAsync(RuleRecord rule);
    Task<bool> DeleteAsync(long id);
}

/// <summary>规则库仓储（P1 只做管理入口，内容 P2 填充）。</summary>
public sealed class RuleRepository : IRuleRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<RuleRepository> _logger;

    public RuleRepository(IDbConnectionFactory factory, ILogger<RuleRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RuleRecord>> ListAsync()
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<RuleRecord>(
            "SELECT * FROM rules ORDER BY category, name");
        var list = rows.AsList();
        _logger.LogDebug("Listed {Count} rules in {ElapsedMs}ms", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task<IReadOnlyList<RuleRecord>> ListEnabledAsync()
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<RuleRecord>(
            "SELECT * FROM rules WHERE enabled = 1 ORDER BY category, name");
        return rows.AsList();
    }

    public Task<RuleRecord> InsertAsync(string category, string name, string body)
        => InsertAsync(new RuleRecord { Category = category, Name = name, Body = body });

    public async Task<RuleRecord> InsertAsync(RuleRecord rule)
    {
        var sw = Stopwatch.StartNew();
        rule.Version = rule.Version <= 0 ? 1 : rule.Version;
        rule.CreatedAt = string.IsNullOrEmpty(rule.CreatedAt) ? DateTimeOffset.UtcNow.ToString("o") : rule.CreatedAt;

        using var connection = _factory.Create();
        rule.Id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO rules (category, name, body, rule_type, severity, target_key, expected, match_hint, enabled, version, created_at)
            VALUES (@Category, @Name, @Body, @RuleType, @Severity, @TargetKey, @Expected, @MatchHint, @Enabled, @Version, @CreatedAt);
            SELECT last_insert_rowid();
            """, rule);
        _logger.LogInformation("Inserted rule {Category}/{Name} type={RuleType} (id={Id}) in {ElapsedMs}ms", rule.Category, rule.Name, rule.RuleType, rule.Id, sw.ElapsedMilliseconds);
        return rule;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var affected = await connection.ExecuteAsync("DELETE FROM rules WHERE id = @id", new { id });
        var deleted = affected > 0;
        _logger.LogInformation("Delete rule {Id}: {Deleted} in {ElapsedMs}ms", id, deleted, sw.ElapsedMilliseconds);
        return deleted;
    }
}

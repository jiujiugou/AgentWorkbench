using Dapper;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentWorkbench.Persistence;

public interface IReviewRepository
{
    Task<ReviewRunRecord> CreateRunAsync(string documentId, string mode, string? template);
    Task<ReviewRunRecord?> GetRunAsync(long id);
    Task<IReadOnlyList<ReviewRunRecord>> ListRunsAsync(string? documentId = null);
    Task UpdateRunAsync(ReviewRunRecord run);
    Task<bool> DeleteRunAsync(long id);

    Task ReplaceElementsAsync(long runId, IReadOnlyList<ReviewElementRecord> elements);
    Task<IReadOnlyList<ReviewElementRecord>> GetElementsAsync(long runId);
    Task<int> CountElementsAsync(long runId);

    Task ReplaceFindingsAsync(long runId, IReadOnlyList<ReviewFindingRecord> findings);
    Task<IReadOnlyList<ReviewFindingRecord>> GetFindingsAsync(long runId);
    Task<int> CountFindingsAsync(long runId);
}

/// <summary>P2 审查任务仓储（运行 / 要素 / 清单）。</summary>
public sealed class ReviewRepository : IReviewRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<ReviewRepository> _logger;

    public ReviewRepository(IDbConnectionFactory factory, ILogger<ReviewRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<ReviewRunRecord> CreateRunAsync(string documentId, string mode, string? template)
    {
        var sw = Stopwatch.StartNew();
        var run = new ReviewRunRecord
        {
            DocumentId = documentId,
            Mode = mode,
            Template = template,
            Status = ReviewStatuses.Pending,
            Steps = "[]",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };

        using var connection = _factory.Create();
        run.Id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO review_runs (document_id, mode, template, status, steps, created_at)
            VALUES (@DocumentId, @Mode, @Template, @Status, @Steps, @CreatedAt);
            SELECT last_insert_rowid();
            """, run);
        _logger.LogInformation("Created review run {RunId} doc={DocumentId} mode={Mode} in {ElapsedMs}ms", run.Id, documentId, mode, sw.ElapsedMilliseconds);
        return run;
    }

    public async Task<ReviewRunRecord?> GetRunAsync(long id)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<ReviewRunRecord>(
            "SELECT * FROM review_runs WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<ReviewRunRecord>> ListRunsAsync(string? documentId = null)
    {
        using var connection = _factory.Create();
        var rows = documentId is null
            ? await connection.QueryAsync<ReviewRunRecord>("SELECT * FROM review_runs ORDER BY id DESC")
            : await connection.QueryAsync<ReviewRunRecord>(
                "SELECT * FROM review_runs WHERE document_id = @documentId ORDER BY id DESC", new { documentId });
        return rows.AsList();
    }

    public async Task UpdateRunAsync(ReviewRunRecord run)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE review_runs
            SET status = @Status,
                steps = @Steps,
                provider_id = @ProviderId,
                model = @Model,
                error = @Error,
                latency_ms = @LatencyMs,
                finished_at = @FinishedAt
            WHERE id = @Id
            """, run);
    }

    public async Task<bool> DeleteRunAsync(long id)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteAsync("DELETE FROM review_runs WHERE id = @id", new { id }) > 0;
    }

    public async Task ReplaceElementsAsync(long runId, IReadOnlyList<ReviewElementRecord> elements)
    {
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM review_elements WHERE run_id = @runId", new { runId }, transaction);
        if (elements.Count > 0)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO review_elements
                    (run_id, key, label, value, value_type, unit, source, confidence, chunk_id, seq, page_from, page_to, evidence, created_at)
                VALUES
                    (@RunId, @Key, @Label, @Value, @ValueType, @Unit, @Source, @Confidence, @ChunkId, @Seq, @PageFrom, @PageTo, @Evidence, @CreatedAt)
                """, elements.Select(e => new { e.RunId, e.Key, e.Label, e.Value, e.ValueType, e.Unit, e.Source, e.Confidence, e.ChunkId, e.Seq, e.PageFrom, e.PageTo, e.Evidence, e.CreatedAt }), transaction);
        }
        transaction.Commit();
        _logger.LogInformation("Replaced {Count} review elements for run {RunId}", elements.Count, runId);
    }

    public async Task<IReadOnlyList<ReviewElementRecord>> GetElementsAsync(long runId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ReviewElementRecord>(
            """
            SELECT id, run_id, key, label, value, value_type, unit, source,
                   CAST(confidence AS REAL) AS confidence, chunk_id, seq, page_from, page_to, evidence, created_at
            FROM review_elements WHERE run_id = @runId ORDER BY id
            """, new { runId });
        return rows.AsList();
    }

    public async Task<int> CountElementsAsync(long runId)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM review_elements WHERE run_id = @runId", new { runId });
    }

    public async Task ReplaceFindingsAsync(long runId, IReadOnlyList<ReviewFindingRecord> findings)
    {
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM review_findings WHERE run_id = @runId", new { runId }, transaction);
        if (findings.Count > 0)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO review_findings
                    (run_id, rule_id, severity, category, title, detail, expected, actual, chunk_id, seq, page_from, page_to, evidence, created_at)
                VALUES
                    (@RunId, @RuleId, @Severity, @Category, @Title, @Detail, @Expected, @Actual, @ChunkId, @Seq, @PageFrom, @PageTo, @Evidence, @CreatedAt)
                """, findings.Select(f => new { f.RunId, f.RuleId, f.Severity, f.Category, f.Title, f.Detail, f.Expected, f.Actual, f.ChunkId, f.Seq, f.PageFrom, f.PageTo, f.Evidence, f.CreatedAt }), transaction);
        }
        transaction.Commit();
        _logger.LogInformation("Replaced {Count} review findings for run {RunId}", findings.Count, runId);
    }

    public async Task<IReadOnlyList<ReviewFindingRecord>> GetFindingsAsync(long runId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ReviewFindingRecord>(
            "SELECT * FROM review_findings WHERE run_id = @runId ORDER BY id", new { runId });
        return rows.AsList();
    }

    public async Task<int> CountFindingsAsync(long runId)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM review_findings WHERE run_id = @runId", new { runId });
    }
}

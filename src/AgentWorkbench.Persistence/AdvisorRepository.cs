using Dapper;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentWorkbench.Persistence;

public interface IAdvisorRepository
{
    Task<AdvisorJobRecord> UpsertJobAsync(long runId);
    Task<AdvisorJobRecord?> GetJobAsync(long runId);
    Task UpdateJobAsync(AdvisorJobRecord job);

    Task ReplaceConclusionsAsync(long runId, IReadOnlyList<ReviewConclusionRecord> conclusions);
    Task<IReadOnlyList<ReviewConclusionRecord>> GetConclusionsAsync(long runId);
    Task<int> CountConclusionsAsync(long runId);

    Task ReplaceStepsAsync(long runId, IReadOnlyList<ReviewAgentStepRecord> steps);
    Task<IReadOnlyList<ReviewAgentStepRecord>> GetStepsAsync(long runId);
}

/// <summary>P3 审查结论 / 轨迹 / 任务仓储。</summary>
public sealed class AdvisorRepository : IAdvisorRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<AdvisorRepository> _logger;

    public AdvisorRepository(IDbConnectionFactory factory, ILogger<AdvisorRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<AdvisorJobRecord> UpsertJobAsync(long runId)
    {
        var sw = Stopwatch.StartNew();
        var job = new AdvisorJobRecord
        {
            RunId = runId,
            Status = AdvisorJobStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM advisor_jobs WHERE run_id = @runId", new { runId }, transaction);
        job.Id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO advisor_jobs (run_id, status, error, created_at, finished_at)
            VALUES (@RunId, @Status, @Error, @CreatedAt, @FinishedAt);
            SELECT last_insert_rowid();
            """, job, transaction);
        transaction.Commit();
        _logger.LogInformation("Upserted advisor job {JobId} for run {RunId} in {ElapsedMs}ms", job.Id, runId, sw.ElapsedMilliseconds);
        return job;
    }

    public async Task<AdvisorJobRecord?> GetJobAsync(long runId)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<AdvisorJobRecord>(
            "SELECT * FROM advisor_jobs WHERE run_id = @runId ORDER BY id DESC LIMIT 1", new { runId });
    }

    public async Task UpdateJobAsync(AdvisorJobRecord job)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE advisor_jobs SET status = @Status, error = @Error, finished_at = @FinishedAt WHERE id = @Id
            """, job);
    }

    public async Task ReplaceConclusionsAsync(long runId, IReadOnlyList<ReviewConclusionRecord> conclusions)
    {
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM review_conclusions WHERE run_id = @runId", new { runId }, transaction);
        if (conclusions.Count > 0)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO review_conclusions
                    (run_id, finding_id, status, title, conclusion, suggestion, impact, basis, confidence, source,
                     chunk_id, seq, page_from, page_to, evidence, citations, prompt_version, steps_used, created_at)
                VALUES
                    (@RunId, @FindingId, @Status, @Title, @Conclusion, @Suggestion, @Impact, @Basis, @Confidence, @Source,
                     @ChunkId, @Seq, @PageFrom, @PageTo, @Evidence, @Citations, @PromptVersion, @StepsUsed, @CreatedAt)
                """, conclusions.Select(c => new
                {
                    c.RunId, c.FindingId, c.Status, c.Title, c.Conclusion, c.Suggestion, c.Impact, c.Basis,
                    c.Confidence, c.Source, c.ChunkId, c.Seq, c.PageFrom, c.PageTo, c.Evidence, c.Citations,
                    c.PromptVersion, c.StepsUsed, c.CreatedAt
                }), transaction);
        }
        transaction.Commit();
        _logger.LogInformation("Replaced {Count} conclusions for run {RunId}", conclusions.Count, runId);
    }

    public async Task<IReadOnlyList<ReviewConclusionRecord>> GetConclusionsAsync(long runId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ReviewConclusionRecord>(
            """
            SELECT id, run_id, finding_id, status, title, conclusion, suggestion, impact, basis,
                   CAST(confidence AS REAL) AS confidence, source, chunk_id, seq, page_from, page_to,
                   evidence, citations, prompt_version, steps_used, created_at
            FROM review_conclusions WHERE run_id = @runId ORDER BY id
            """, new { runId });
        return rows.AsList();
    }

    public async Task<int> CountConclusionsAsync(long runId)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM review_conclusions WHERE run_id = @runId", new { runId });
    }

    public async Task ReplaceStepsAsync(long runId, IReadOnlyList<ReviewAgentStepRecord> steps)
    {
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM review_agent_steps WHERE run_id = @runId", new { runId }, transaction);
        if (steps.Count > 0)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO review_agent_steps
                    (run_id, finding_id, step_index, kind, tool, arguments, result, created_at)
                VALUES
                    (@RunId, @FindingId, @StepIndex, @Kind, @Tool, @Arguments, @Result, @CreatedAt)
                """, steps.Select(s => new
                {
                    s.RunId, s.FindingId, s.StepIndex, s.Kind, s.Tool, s.Arguments, s.Result, s.CreatedAt
                }), transaction);
        }
        transaction.Commit();
        _logger.LogInformation("Replaced {Count} agent steps for run {RunId}", steps.Count, runId);
    }

    public async Task<IReadOnlyList<ReviewAgentStepRecord>> GetStepsAsync(long runId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ReviewAgentStepRecord>(
            "SELECT * FROM review_agent_steps WHERE run_id = @runId ORDER BY finding_id, step_index", new { runId });
        return rows.AsList();
    }
}

using Dapper;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Persistence;

/// <summary>Agent Harness 运行 / 轨迹仓储。</summary>
public interface IAgentRunRepository
{
    Task<long> InsertRunAsync(AgentRunRecord run);
    Task InsertStepsAsync(long runId, IReadOnlyList<AgentStepRecord> steps);
    Task<AgentRunRecord?> GetRunAsync(long id);
    Task<IReadOnlyList<AgentStepRecord>> GetStepsAsync(long runId);
}

/// <inheritdoc />
public sealed class AgentRunRepository : IAgentRunRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<AgentRunRepository> _logger;

    public AgentRunRepository(IDbConnectionFactory factory, ILogger<AgentRunRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<long> InsertRunAsync(AgentRunRecord run)
    {
        using var connection = _factory.Create();
        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO agent_runs (session_id, status, iterations, model, latency_ms, error, created_at, finished_at)
            VALUES (@SessionId, @Status, @Iterations, @Model, @LatencyMs, @Error, @CreatedAt, @FinishedAt);
            SELECT last_insert_rowid();
            """, run);
        _logger.LogInformation("Inserted agent run {RunId} session={SessionId} iterations={Iterations}", id, run.SessionId, run.Iterations);
        return id;
    }

    public async Task InsertStepsAsync(long runId, IReadOnlyList<AgentStepRecord> steps)
    {
        if (steps.Count == 0)
            return;

        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_steps (run_id, iteration, kind, tool, arguments, result, created_at)
            VALUES (@RunId, @Iteration, @Kind, @Tool, @Arguments, @Result, @CreatedAt)
            """, steps.Select(s => new
            {
                RunId = runId,
                s.Iteration,
                s.Kind,
                s.Tool,
                s.Arguments,
                s.Result,
                s.CreatedAt
            }));
    }

    public async Task<AgentRunRecord?> GetRunAsync(long id)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<AgentRunRecord>(
            "SELECT * FROM agent_runs WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<AgentStepRecord>> GetStepsAsync(long runId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<AgentStepRecord>(
            "SELECT * FROM agent_steps WHERE run_id = @runId ORDER BY iteration, id", new { runId });
        return rows.AsList();
    }
}

using Dapper;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Persistence;

/// <summary>P4 工作流仓储：定义 / 提示词版本 / 运行 / 步骤。</summary>
public interface IWorkflowRepository
{
    Task<WorkflowDefinitionRecord?> GetDefinitionAsync(string key);
    Task<IReadOnlyList<WorkflowDefinitionRecord>> ListDefinitionsAsync();
    Task UpsertDefinitionAsync(WorkflowDefinitionRecord definition);

    Task<IReadOnlyList<PromptVersionRecord>> ListPromptsAsync(string? promptKey);
    Task<PromptVersionRecord?> GetPromptAsync(string promptKey, string? version);
    Task<PromptVersionRecord> AddPromptAsync(string promptKey, string version, string content, bool active);
    Task<bool> ActivatePromptAsync(string promptKey, string version);

    Task<WorkflowRunRecord> CreateRunAsync(WorkflowRunRecord run);
    Task<WorkflowRunRecord?> GetRunAsync(long id);
    Task<IReadOnlyList<WorkflowRunRecord>> ListRunsAsync(string? documentId);
    Task UpdateRunAsync(WorkflowRunRecord run);
    Task<bool> DeleteRunAsync(long id);

    Task CreateStepsAsync(long runId, IReadOnlyList<WorkflowStepRecord> steps);
    Task<IReadOnlyList<WorkflowStepRecord>> GetStepsAsync(long runId);
    Task UpdateStepAsync(WorkflowStepRecord step);
    Task ResetStepAsync(long stepId);
    Task MarkDownstreamStaleAsync(long runId, int orderIndex);
}

/// <inheritdoc />
public sealed class WorkflowRepository : IWorkflowRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<WorkflowRepository> _logger;

    public WorkflowRepository(IDbConnectionFactory factory, ILogger<WorkflowRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // ---------------- definitions ----------------

    public async Task<WorkflowDefinitionRecord?> GetDefinitionAsync(string key)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<WorkflowDefinitionRecord>(
            "SELECT * FROM workflow_definitions WHERE key = @key AND enabled = 1", new { key });
    }

    public async Task<IReadOnlyList<WorkflowDefinitionRecord>> ListDefinitionsAsync()
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<WorkflowDefinitionRecord>(
            "SELECT * FROM workflow_definitions ORDER BY key");
        return rows.AsList();
    }

    public async Task UpsertDefinitionAsync(WorkflowDefinitionRecord definition)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_definitions (id, key, name, definition, enabled, created_at, updated_at)
            VALUES (@Id, @Key, @Name, @Definition, @Enabled, @CreatedAt, @UpdatedAt)
            ON CONFLICT(key) DO UPDATE SET name = @Name, definition = @Definition, enabled = @Enabled, updated_at = @UpdatedAt
            """, definition);
    }

    // ---------------- prompts ----------------

    public async Task<IReadOnlyList<PromptVersionRecord>> ListPromptsAsync(string? promptKey)
    {
        using var connection = _factory.Create();
        var rows = string.IsNullOrWhiteSpace(promptKey)
            ? await connection.QueryAsync<PromptVersionRecord>("SELECT * FROM prompt_versions ORDER BY prompt_key, version")
            : await connection.QueryAsync<PromptVersionRecord>(
                "SELECT * FROM prompt_versions WHERE prompt_key = @promptKey ORDER BY version", new { promptKey });
        return rows.AsList();
    }

    public async Task<PromptVersionRecord?> GetPromptAsync(string promptKey, string? version)
    {
        using var connection = _factory.Create();
        if (!string.IsNullOrWhiteSpace(version))
        {
            return await connection.QuerySingleOrDefaultAsync<PromptVersionRecord>(
                "SELECT * FROM prompt_versions WHERE prompt_key = @promptKey AND version = @version",
                new { promptKey, version });
        }

        return await connection.QuerySingleOrDefaultAsync<PromptVersionRecord>(
            """
            SELECT * FROM prompt_versions WHERE prompt_key = @promptKey
            ORDER BY active DESC, created_at DESC LIMIT 1
            """, new { promptKey });
    }

    public async Task<PromptVersionRecord> AddPromptAsync(string promptKey, string version, string content, bool active)
    {
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        if (active)
            await connection.ExecuteAsync(
                "UPDATE prompt_versions SET active = 0 WHERE prompt_key = @promptKey", new { promptKey }, transaction);

        var record = new PromptVersionRecord
        {
            PromptKey = promptKey,
            Version = version,
            Content = content,
            Active = active ? 1 : 0,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        record.Id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO prompt_versions (prompt_key, version, content, active, created_at)
            VALUES (@PromptKey, @Version, @Content, @Active, @CreatedAt);
            SELECT last_insert_rowid();
            """, record, transaction);
        transaction.Commit();
        _logger.LogInformation("Upserted prompt version {Key}/{Version} active={Active}", promptKey, version, active);
        return record;
    }

    public async Task<bool> ActivatePromptAsync(string promptKey, string version)
    {
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        var exists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM prompt_versions WHERE prompt_key = @promptKey AND version = @version",
            new { promptKey, version }, transaction);
        if (exists == 0)
        {
            transaction.Rollback();
            return false;
        }

        await connection.ExecuteAsync(
            "UPDATE prompt_versions SET active = 0 WHERE prompt_key = @promptKey", new { promptKey }, transaction);
        await connection.ExecuteAsync(
            "UPDATE prompt_versions SET active = 1 WHERE prompt_key = @promptKey AND version = @version",
            new { promptKey, version }, transaction);
        transaction.Commit();
        return true;
    }

    // ---------------- runs ----------------

    public async Task<WorkflowRunRecord> CreateRunAsync(WorkflowRunRecord run)
    {
        using var connection = _factory.Create();
        run.CreatedAt = DateTimeOffset.UtcNow.ToString("o");
        run.Id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO workflow_runs (workflow_key, document_id, prompt_version, status, error, latency_ms, created_at, finished_at)
            VALUES (@WorkflowKey, @DocumentId, @PromptVersion, @Status, @Error, @LatencyMs, @CreatedAt, @FinishedAt);
            SELECT last_insert_rowid();
            """, run);
        return run;
    }

    public async Task<WorkflowRunRecord?> GetRunAsync(long id)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<WorkflowRunRecord>(
            "SELECT * FROM workflow_runs WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<WorkflowRunRecord>> ListRunsAsync(string? documentId)
    {
        using var connection = _factory.Create();
        var rows = string.IsNullOrWhiteSpace(documentId)
            ? await connection.QueryAsync<WorkflowRunRecord>("SELECT * FROM workflow_runs ORDER BY id DESC")
            : await connection.QueryAsync<WorkflowRunRecord>(
                "SELECT * FROM workflow_runs WHERE document_id = @documentId ORDER BY id DESC", new { documentId });
        return rows.AsList();
    }

    public async Task UpdateRunAsync(WorkflowRunRecord run)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE workflow_runs
            SET status = @Status, error = @Error, latency_ms = @LatencyMs, finished_at = @FinishedAt
            WHERE id = @Id
            """, run);
    }

    public async Task<bool> DeleteRunAsync(long id)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteAsync("DELETE FROM workflow_runs WHERE id = @id", new { id }) > 0;
    }

    // ---------------- steps ----------------

    public async Task CreateStepsAsync(long runId, IReadOnlyList<WorkflowStepRecord> steps)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_steps (run_id, step_key, step_type, order_index, status, attempts)
            VALUES (@RunId, @StepKey, @StepType, @OrderIndex, @Status, @Attempts)
            """, steps.Select(s => new
            {
                RunId = runId,
                s.StepKey,
                s.StepType,
                s.OrderIndex,
                s.Status,
                s.Attempts
            }));
    }

    public async Task<IReadOnlyList<WorkflowStepRecord>> GetStepsAsync(long runId)
    {
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<WorkflowStepRecord>(
            "SELECT * FROM workflow_steps WHERE run_id = @runId ORDER BY order_index", new { runId });
        return rows.AsList();
    }

    public async Task UpdateStepAsync(WorkflowStepRecord step)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE workflow_steps
            SET status = @Status, attempts = @Attempts, input = @Input, output = @Output,
                error = @Error, started_at = @StartedAt, finished_at = @FinishedAt
            WHERE id = @Id
            """, step);
    }

    public async Task ResetStepAsync(long stepId)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE workflow_steps
            SET status = 'pending', attempts = 0, output = NULL, error = NULL, started_at = NULL, finished_at = NULL
            WHERE id = @stepId
            """, new { stepId });
    }

    public async Task MarkDownstreamStaleAsync(long runId, int orderIndex)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE workflow_steps
            SET status = 'stale', output = NULL, error = NULL, started_at = NULL, finished_at = NULL
            WHERE run_id = @runId AND order_index > @orderIndex AND status <> 'stale'
            """, new { runId, orderIndex });
    }
}

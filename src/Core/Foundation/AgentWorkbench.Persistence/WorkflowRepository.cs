using AgentWorkbench.Domain;
using Microsoft.EntityFrameworkCore;
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
/// <remarks>
/// <para><b>EF Core 实现</b>（Dapper 版换成 LINQ 的试点）。改变的是"怎么表达查询"，不是行为：
/// 同一条业务意图仍然产生同样的 SQL 语义，只是字段名与参数绑定交给编译器检查。</para>
/// <para><b>为什么用 <see cref="IDbContextFactory{TContext}"/></b>：本类注册为 Singleton
/// （3 个 BackgroundService 都在用它），而 <c>DbContext</c> 是 Scoped，直接注入会在启动时抛
/// "Cannot consume scoped service from singleton"。工厂模式每次方法自建一个上下文，
/// 开销与原来每次 <c>IDbConnectionFactory.Create()</c> 开连接相当。</para>
/// <para><b>局部更新优先用 ExecuteUpdate/ExecuteDelete</b>：不加载实体、不发 SELECT，
/// 且表达的是"改哪几列"，与原来的 <c>UPDATE ... SET 指定列</c> 完全等价。</para>
/// </remarks>
public sealed class WorkflowRepository(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<WorkflowRepository> logger) : IWorkflowRepository
{
    // ---------------- definitions ----------------

    public async Task<WorkflowDefinitionRecord?> GetDefinitionAsync(string key)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.WorkflowDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Key == key && d.Enabled == 1);
    }

    public async Task<IReadOnlyList<WorkflowDefinitionRecord>> ListDefinitionsAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.WorkflowDefinitions
            .AsNoTracking()
            .OrderBy(d => d.Key)
            .ToListAsync();
    }

    /// <remarks>
    /// 原来是一条 <c>INSERT ... ON CONFLICT(key) DO UPDATE</c>。EF 没有原生 upsert，
    /// 这里退化成"先查再改"，多一次 SELECT 换来表达清晰——这也是本次试点里唯一明显变啰嗦的方法。
    /// </remarks>
    public async Task UpsertDefinitionAsync(WorkflowDefinitionRecord definition)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var existing = await db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.Key == definition.Key);

        if (existing is null)
        {
            db.WorkflowDefinitions.Add(definition);
        }
        else
        {
            existing.Name = definition.Name;
            existing.Definition = definition.Definition;
            existing.Enabled = definition.Enabled;
            existing.UpdatedAt = definition.UpdatedAt;
        }

        await db.SaveChangesAsync();
    }

    // ---------------- prompts ----------------

    public async Task<IReadOnlyList<PromptVersionRecord>> ListPromptsAsync(string? promptKey)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var query = db.PromptVersions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(promptKey))
            query = query.Where(p => p.PromptKey == promptKey);

        return await query
            .OrderBy(p => p.PromptKey)
            .ThenBy(p => p.Version)
            .ToListAsync();
    }

    public async Task<PromptVersionRecord?> GetPromptAsync(string promptKey, string? version)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var query = db.PromptVersions
            .AsNoTracking()
            .Where(p => p.PromptKey == promptKey);

        return string.IsNullOrWhiteSpace(version)
            ? await query
                .OrderByDescending(p => p.Active)
                .ThenByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync()
            : await query.FirstOrDefaultAsync(p => p.Version == version);
    }

    public async Task<PromptVersionRecord> AddPromptAsync(
        string promptKey, string version, string content, bool active)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        if (active)
        {
            await db.PromptVersions
                .Where(p => p.PromptKey == promptKey)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Active, 0));
        }

        var record = new PromptVersionRecord
        {
            PromptKey = promptKey,
            Version = version,
            Content = content,
            Active = active ? 1 : 0,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };

        db.PromptVersions.Add(record);
        await db.SaveChangesAsync(); // Id 由 SQLite 回填，无需 SELECT last_insert_rowid()

        await transaction.CommitAsync();
        logger.LogInformation("Upserted prompt version {Key}/{Version} active={Active}", promptKey, version, active);
        return record;
    }

    public async Task<bool> ActivatePromptAsync(string promptKey, string version)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var exists = await db.PromptVersions
            .AnyAsync(p => p.PromptKey == promptKey && p.Version == version);
        if (!exists)
            return false; // 事务未提交即释放，等价于原来的 Rollback

        await db.PromptVersions
            .Where(p => p.PromptKey == promptKey)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Active, 0));

        await db.PromptVersions
            .Where(p => p.PromptKey == promptKey && p.Version == version)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Active, 1));

        await transaction.CommitAsync();
        return true;
    }

    // ---------------- runs ----------------

    public async Task<WorkflowRunRecord> CreateRunAsync(WorkflowRunRecord run)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        run.CreatedAt = DateTimeOffset.UtcNow.ToString("o");
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync(); // 自增 Id 回填到 run.Id

        return run;
    }

    public async Task<WorkflowRunRecord?> GetRunAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<IReadOnlyList<WorkflowRunRecord>> ListRunsAsync(string? documentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var query = db.WorkflowRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(documentId))
            query = query.Where(r => r.DocumentId == documentId);

        return await query.OrderByDescending(r => r.Id).ToListAsync();
    }

    /// <remarks>只更新变化的那 4 列，不加载实体——与原来手工指定 SET 列一致。</remarks>
    public async Task UpdateRunAsync(WorkflowRunRecord run)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        await db.WorkflowRuns
            .Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, run.Status)
                .SetProperty(r => r.Error, run.Error)
                .SetProperty(r => r.LatencyMs, run.LatencyMs)
                .SetProperty(r => r.FinishedAt, run.FinishedAt));
    }

    public async Task<bool> DeleteRunAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var affected = await db.WorkflowRuns
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync();

        return affected > 0;
    }

    // ---------------- steps ----------------

    public async Task CreateStepsAsync(long runId, IReadOnlyList<WorkflowStepRecord> steps)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var records = steps.Select(s => new WorkflowStepRecord
        {
            RunId = runId,
            StepKey = s.StepKey,
            StepType = s.StepType,
            OrderIndex = s.OrderIndex,
            Status = s.Status,
            Attempts = s.Attempts
        });

        db.WorkflowSteps.AddRange(records);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<WorkflowStepRecord>> GetStepsAsync(long runId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.WorkflowSteps
            .AsNoTracking()
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync();
    }

    public async Task UpdateStepAsync(WorkflowStepRecord step)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        await db.WorkflowSteps
            .Where(s => s.Id == step.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, step.Status)
                .SetProperty(x => x.Attempts, step.Attempts)
                .SetProperty(x => x.Input, step.Input)
                .SetProperty(x => x.Output, step.Output)
                .SetProperty(x => x.Error, step.Error)
                .SetProperty(x => x.StartedAt, step.StartedAt)
                .SetProperty(x => x.FinishedAt, step.FinishedAt));
    }

    public async Task ResetStepAsync(long stepId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        await db.WorkflowSteps
            .Where(s => s.Id == stepId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, WorkflowStepStatuses.Pending)
                .SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.Output, (string?)null)
                .SetProperty(x => x.Error, (string?)null)
                .SetProperty(x => x.StartedAt, (string?)null)
                .SetProperty(x => x.FinishedAt, (string?)null));
    }

    public async Task MarkDownstreamStaleAsync(long runId, int orderIndex)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        await db.WorkflowSteps
            .Where(s => s.RunId == runId
                        && s.OrderIndex > orderIndex
                        && s.Status != WorkflowStepStatuses.Stale)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, WorkflowStepStatuses.Stale)
                .SetProperty(x => x.Output, (string?)null)
                .SetProperty(x => x.Error, (string?)null)
                .SetProperty(x => x.StartedAt, (string?)null)
                .SetProperty(x => x.FinishedAt, (string?)null));
    }
}

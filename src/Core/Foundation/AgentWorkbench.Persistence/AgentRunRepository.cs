using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

/// <summary>Agent Harness 运行 / 轨迹仓储。</summary>
public interface IAgentRunRepository
{
    Task<long> InsertRunAsync(AgentRunRecord run);
    Task InsertStepsAsync(long runId, IReadOnlyList<AgentStepRecord> steps);
    Task<AgentRunRecord?> GetRunAsync(long id);
    Task<IReadOnlyList<AgentStepRecord>> GetStepsAsync(long runId);

    /// <summary>按会话 / 终态检索运行记录（最近优先，最多 <paramref name="limit"/> 条）；两个过滤条件传 <c>null</c> 表示不过滤。</summary>
    Task<IReadOnlyList<AgentRunRecord>> QueryRunsAsync(string? sessionId, string? status, int limit);
}

/// <inheritdoc />
/// <remarks>
/// EF Core 实现。<c>Id</c> 不再需要 <c>SELECT last_insert_rowid()</c>：
/// <c>SaveChangesAsync</c> 之后自增主键会回填到实体上。
/// </remarks>
public sealed class AgentRunRepository(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<AgentRunRepository> logger) : IAgentRunRepository
{
    public async Task<long> InsertRunAsync(AgentRunRecord run)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Inserted agent run {RunId} session={SessionId} iterations={Iterations}",
            run.Id, run.SessionId, run.Iterations);
        return run.Id;
    }

    public async Task InsertStepsAsync(long runId, IReadOnlyList<AgentStepRecord> steps)
    {
        if (steps.Count == 0)
            return;

        await using var db = await contextFactory.CreateDbContextAsync();
        foreach (var step in steps)
            step.RunId = runId;

        db.AgentSteps.AddRange(steps);
        await db.SaveChangesAsync();
    }

    public async Task<AgentRunRecord?> GetRunAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<IReadOnlyList<AgentStepRecord>> GetStepsAsync(long runId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.AgentSteps
            .AsNoTracking()
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.Iteration).ThenBy(s => s.Id)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AgentRunRecord>> QueryRunsAsync(string? sessionId, string? status, int limit)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        // 两个条件都是「传 null 表示不过滤」，与原来的 (@x IS NULL OR col = @x) 等价。
        // 注意是判 null 不是判空串：空串在原 SQL 里是「过滤出 session_id = ''」，不是「不过滤」。
        var query = db.AgentRuns.AsNoTracking();
        if (sessionId is not null)
            query = query.Where(r => r.SessionId == sessionId);
        if (status is not null)
            query = query.Where(r => r.Status == status);

        return await query.OrderByDescending(r => r.Id).Take(limit).ToListAsync();
    }
}

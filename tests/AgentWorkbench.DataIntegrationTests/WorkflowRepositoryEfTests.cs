using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.DataIntegrationTests;

/// <summary>
/// EF 版 WorkflowRepository 的行为验证：跑真实 SQLite，确认 LINQ 翻译出来的语句
/// 与原来手写 SQL 语义一致。
/// </summary>
/// <remarks>
/// 只测最容易翻译错的地方：自增回填、局部更新（不能顺手写回别的列）、
/// 批量条件更新、以及事务内的互斥激活。
/// </remarks>
public sealed class WorkflowRepositoryEfTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public WorkflowRepositoryEfTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aw-wf-{Guid.NewGuid():N}.db");

        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            ForeignKeys = true,
            Pooling = false
        }.ToString();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _factory = new TestContextFactory(options);

        using var db = _factory.CreateDbContext();
        // 只建本仓储用到的 4 张表，DDL 与迁移生成的一致（列名、可空性、自增）
        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE workflow_definitions (
                id TEXT NOT NULL PRIMARY KEY, key TEXT NOT NULL, name TEXT NOT NULL,
                definition TEXT NOT NULL, enabled INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE prompt_versions (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                prompt_key TEXT NOT NULL, version TEXT NOT NULL, content TEXT NOT NULL,
                active INTEGER NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE workflow_runs (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                workflow_key TEXT NOT NULL, document_id TEXT NOT NULL, prompt_version TEXT,
                status TEXT NOT NULL, error TEXT, latency_ms INTEGER,
                created_at TEXT NOT NULL, finished_at TEXT);
            CREATE TABLE workflow_steps (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL, step_key TEXT NOT NULL, step_type TEXT NOT NULL,
                order_index INTEGER NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL,
                input TEXT, output TEXT, error TEXT, started_at TEXT, finished_at TEXT);
            """);
    }

    private WorkflowRepository Repo() => new(_factory, NullLogger<WorkflowRepository>.Instance);

    [Fact]
    public async Task CreateRunAsync_回填自增Id()
    {
        var run = await Repo().CreateRunAsync(new WorkflowRunRecord
        {
            WorkflowKey = "wf-a",
            DocumentId = "doc-1",
            Status = WorkflowRunStatuses.Pending
        });

        Assert.True(run.Id > 0);
        Assert.False(string.IsNullOrEmpty(run.CreatedAt));

        var reloaded = await Repo().GetRunAsync(run.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("wf-a", reloaded!.WorkflowKey);
    }

    [Fact]
    public async Task UpdateRunAsync_只改四个状态列_不改工作流标识()
    {
        var repo = Repo();
        var run = await repo.CreateRunAsync(new WorkflowRunRecord
        {
            WorkflowKey = "wf-b",
            DocumentId = "doc-2",
            Status = WorkflowRunStatuses.Running
        });

        run.Status = WorkflowRunStatuses.Succeeded;
        run.Error = "boom";
        run.LatencyMs = 1234;
        run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
        await repo.UpdateRunAsync(run);

        var reloaded = await Repo().GetRunAsync(run.Id);
        Assert.Equal(WorkflowRunStatuses.Succeeded, reloaded!.Status);
        Assert.Equal("boom", reloaded.Error);
        Assert.Equal(1234, reloaded.LatencyMs);
        // 不该被顺手改掉的列
        Assert.Equal("wf-b", reloaded.WorkflowKey);
        Assert.Equal("doc-2", reloaded.DocumentId);
    }

    [Fact]
    public async Task MarkDownstreamStaleAsync_只影响下游且清空结果()
    {
        var repo = Repo();
        var run = await repo.CreateRunAsync(new WorkflowRunRecord
        {
            WorkflowKey = "wf-c",
            DocumentId = "doc-3"
        });

        await repo.CreateStepsAsync(run.Id, [
            new WorkflowStepRecord { StepKey = "s1", StepType = "t", OrderIndex = 0, Status = WorkflowStepStatuses.Pending },
            new WorkflowStepRecord { StepKey = "s2", StepType = "t", OrderIndex = 1, Status = WorkflowStepStatuses.Pending },
            new WorkflowStepRecord { StepKey = "s3", StepType = "t", OrderIndex = 2, Status = WorkflowStepStatuses.Pending }
        ]);

        // 模拟三步都执行完（CreateSteps 只插初始列，输出是后续 UpdateStep 写进去的）
        foreach (var step in await Repo().GetStepsAsync(run.Id))
        {
            step.Status = WorkflowStepStatuses.Succeeded;
            step.Output = $"o{step.OrderIndex}";
            await Repo().UpdateStepAsync(step);
        }

        await repo.MarkDownstreamStaleAsync(run.Id, orderIndex: 0);

        var steps = await Repo().GetStepsAsync(run.Id);
        Assert.Equal(WorkflowStepStatuses.Succeeded, steps[0].Status);
        Assert.Equal("o0", steps[0].Output);
        Assert.Equal(WorkflowStepStatuses.Stale, steps[1].Status);
        Assert.Null(steps[1].Output); // SET output = NULL 必须真的写 null，不能变成跳过
        Assert.Equal(WorkflowStepStatuses.Stale, steps[2].Status);
        Assert.Null(steps[2].Error);
    }

    [Fact]
    public async Task ActivatePromptAsync_同键互斥且事务生效()
    {
        var repo = Repo();
        await repo.AddPromptAsync("p", "v1", "内容1", active: true);
        await repo.AddPromptAsync("p", "v2", "内容2", active: true); // 激活 v2 应把 v1 置 0

        var v1 = await Repo().GetPromptAsync("p", "v1");
        var v2 = await Repo().GetPromptAsync("p", "v2");
        Assert.Equal(0, v1!.Active);
        Assert.Equal(1, v2!.Active);

        // 默认版本应取 active 的那条
        var latest = await Repo().GetPromptAsync("p", null);
        Assert.Equal("v2", latest!.Version);

        Assert.True(await Repo().ActivatePromptAsync("p", "v1"));
        Assert.Equal(1, (await Repo().GetPromptAsync("p", "v1"))!.Active);
        Assert.Equal(0, (await Repo().GetPromptAsync("p", "v2"))!.Active);

        // 不存在的版本：返回 false，且不改动任何 active
        Assert.False(await Repo().ActivatePromptAsync("p", "v9"));
        Assert.Equal(1, (await Repo().GetPromptAsync("p", "v1"))!.Active);
    }

    [Fact]
    public async Task UpsertDefinitionAsync_按key新增或更新()
    {
        var repo = Repo();
        await repo.UpsertDefinitionAsync(new WorkflowDefinitionRecord
        {
            Id = Guid.NewGuid().ToString("N"), Key = "k1", Name = "旧名",
            Definition = "{}", Enabled = 1,
            CreatedAt = "t0", UpdatedAt = "t1"
        });

        await repo.UpsertDefinitionAsync(new WorkflowDefinitionRecord
        {
            Id = Guid.NewGuid().ToString("N"), Key = "k1", Name = "新名",
            Definition = "{\"a\":1}", Enabled = 0,
            CreatedAt = "t0", UpdatedAt = "t2"
        });

        var all = await Repo().ListDefinitionsAsync();
        Assert.Single(all); // key 冲突走更新，不新增第二行
        Assert.Equal("新名", all[0].Name);
        Assert.Equal(0, all[0].Enabled);

        // enabled = 0 的不能被 GetDefinitionAsync 命中（与原 SQL 的 enabled = 1 一致）
        Assert.Null(await Repo().GetDefinitionAsync("k1"));
    }

    [Fact]
    public async Task DeleteRunAsync_不存在时返回false()
    {
        var repo = Repo();
        var run = await repo.CreateRunAsync(new WorkflowRunRecord { WorkflowKey = "wf-d", DocumentId = "d" });

        Assert.True(await repo.DeleteRunAsync(run.Id));
        Assert.False(await repo.DeleteRunAsync(run.Id));
        Assert.Null(await Repo().GetRunAsync(run.Id));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _dbPath + suffix;
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}

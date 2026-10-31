using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentWorkbench.DataIntegrationTests;

/// <summary>
/// AppDbContext 映射的哨兵：表名与列名靠 snake_case 约定生成，约定一旦失效
/// （改了属性名没改库、或漏配 ToTable），这里必须红。
/// </summary>
/// <remarks>
/// 最后一组用例会真的连本地库发 SELECT——EF 生成的列名若与 schema 不符，
/// SQLite 会直接报 "no such column"，这是唯一能抓住「映射看着对、实际对不上」的办法。
/// </remarks>
public sealed class AppDbContextMappingTests
{
    private static readonly string[] ExpectedTables =
    [
        "agent_runs", "agent_steps", "chunks", "conversations", "document_pages", "documents",
        "messages", "prompt_versions", "provider_models", "providers", "tenants", "users",
        "workflow_definitions", "workflow_runs", "workflow_steps"
    ];

    [Fact]
    public void 映射覆盖全部实体表且表名与既有schema一致()
    {
        using var db = Create("Data Source=:memory:");

        var tables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(ExpectedTables.OrderBy(name => name), tables);
    }

    [Fact]
    public void 列名按蛇形约定映射()
    {
        using var db = Create("Data Source=:memory:");

        var chunkColumns = Columns(db, typeof(ChunkRecord));
        Assert.Contains("document_id", chunkColumns);
        Assert.Contains("page_from", chunkColumns);
        Assert.Contains("char_count", chunkColumns);

        var stepColumns = Columns(db, typeof(WorkflowStepRecord));
        Assert.Contains("run_id", stepColumns);
        Assert.Contains("order_index", stepColumns);
        Assert.Contains("step_key", stepColumns);

        // 会话隔离列：Domain 类型不补这两个属性时，EF 会把它们整个漏掉。
        var conversationColumns = Columns(db, typeof(ConversationRecord));
        Assert.Contains("tenant_id", conversationColumns);
        Assert.Contains("user_id", conversationColumns);
    }

    [Fact]
    public void 虚表不在映射范围内()
    {
        using var db = Create("Data Source=:memory:");

        var tables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName()!)
            .ToHashSet();

        // FTS5 与 vec0 是虚拟表，EF 映射不了，必须走 Database.SqlQuery<T>。
        Assert.DoesNotContain("chunks_fts", tables);
        Assert.DoesNotContain("chunks_vec", tables);
        Assert.DoesNotContain("vec_chunks_vec", tables);
    }

    [Fact]
    public async Task 真实库能被EF读出且列名全部对得上()
    {
        var dbPath = Path.Combine(
            FindRepoRoot(), "src", "AgentWorkbench.Webapi", "data", "agentworkbench.db");

        if (!File.Exists(dbPath))
            return; // 无本地库（CI / 新克隆）时跳过，不把环境依赖变红

        await using var db = Create($"Data Source={dbPath};Mode=ReadOnly");

        // 每张表发一次真实 SELECT：列名与 schema 不符会在这里抛 SqliteException。
        var counts = new[]
        {
            await db.Documents.CountAsync(),
            await db.DocumentPages.CountAsync(),
            await db.Chunks.CountAsync(),
            await db.Providers.CountAsync(),
            await db.ProviderModels.CountAsync(),
            await db.AgentRuns.CountAsync(),
            await db.AgentSteps.CountAsync(),
            await db.Conversations.CountAsync(),
            await db.Messages.CountAsync(),
            await db.Tenants.CountAsync(),
            await db.Users.CountAsync(),
            await db.WorkflowDefinitions.CountAsync(),
            await db.PromptVersions.CountAsync(),
            await db.WorkflowRuns.CountAsync(),
            await db.WorkflowSteps.CountAsync()
        };

        Assert.All(counts, count => Assert.True(count >= 0));
    }

    private static AppDbContext Create(string connectionString)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static HashSet<string> Columns(AppDbContext db, Type entity)
        => db.Model.FindEntityType(entity)!
            .GetProperties()
            .Select(p => p.GetColumnName())
            .ToHashSet();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "未能从测试输出目录向上找到 AgentWorkbench.slnx。");
        return dir!.FullName;
    }
}

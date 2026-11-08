using AgentWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

/// <summary>
/// EF Core 映射层：只做读写映射，<b>不做建表、不做迁移</b>。
/// </summary>
/// <remarks>
/// <para><b>唯一 schema 真相仍是 FluentMigrator</b>（Migrations/M0xx）。一旦这里出现
/// <c>Database.Migrate()</c> 或 <c>EnsureCreated()</c>，就有两套建表来源，漂移只是时间问题；
/// 因此本项目<b>不引用</b>任何 EF 迁移包，也没有 Migrations 目录。</para>
/// <para><b>实体直接用 Domain 类型</b>：Domain 里这些都是无行为 POCO（可写属性 + 无参构造），
/// 不需要在 Persistence 里再造一层 Entity。代价是从此 Domain 类型一旦改成只读 record，
/// 这里就要改用 backing field 配置——所以 Domain 侧不要加 EF 特性（架构测试禁止 Domain 依赖基础设施）。</para>
/// <para><b>列名靠命名约定</b>：<c>UseSnakeCaseNamingConvention()</c> 把 <c>DocumentId → document_id</c>、
/// <c>OrderIndex → order_index</c> 全部自动对上，因此下面不需要写任何 <c>HasColumnName</c>。</para>
/// <para><b>不映射的表</b>：<c>chunks_fts</c> / <c>chunks_vec</c> / <c>vec_*</c>（虚表，EF 无法映射，
/// 一律走 <c>db.Database.SqlQuery&lt;T&gt;</c>）；<c>agent_sessions</c> / <c>memory_items</c>
/// （由 Memory.Persistence 自己负责，Persistence 不反向依赖它们）。</para>
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
    public DbSet<DocumentPageRecord> DocumentPages => Set<DocumentPageRecord>();
    public DbSet<ChunkRecord> Chunks => Set<ChunkRecord>();

    public DbSet<ProviderRecord> Providers => Set<ProviderRecord>();
    public DbSet<ProviderModelRecord> ProviderModels => Set<ProviderModelRecord>();

    public DbSet<AgentRunRecord> AgentRuns => Set<AgentRunRecord>();
    public DbSet<AgentStepRecord> AgentSteps => Set<AgentStepRecord>();

    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();

    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
    public DbSet<UserRecord> Users => Set<UserRecord>();

    public DbSet<WorkflowDefinitionRecord> WorkflowDefinitions => Set<WorkflowDefinitionRecord>();
    public DbSet<PromptVersionRecord> PromptVersions => Set<PromptVersionRecord>();
    public DbSet<WorkflowRunRecord> WorkflowRuns => Set<WorkflowRunRecord>();
    public DbSet<WorkflowStepRecord> WorkflowSteps => Set<WorkflowStepRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 主键与自增：string 主键由调用方生成（uuid），long 主键由 SQLite 自增。
        Map<DocumentRecord>(modelBuilder, "documents", generated: false);
        Map<DocumentPageRecord>(modelBuilder, "document_pages");
        Map<ChunkRecord>(modelBuilder, "chunks");

        Map<ProviderRecord>(modelBuilder, "providers");
        Map<ProviderModelRecord>(modelBuilder, "provider_models");

        Map<AgentRunRecord>(modelBuilder, "agent_runs");
        Map<AgentStepRecord>(modelBuilder, "agent_steps");

        Map<ConversationRecord>(modelBuilder, "conversations", generated: false);
        Map<MessageRecord>(modelBuilder, "messages");

        Map<TenantRecord>(modelBuilder, "tenants", generated: false);
        Map<UserRecord>(modelBuilder, "users", generated: false);

        Map<WorkflowDefinitionRecord>(modelBuilder, "workflow_definitions", generated: false);
        Map<PromptVersionRecord>(modelBuilder, "prompt_versions");
        Map<WorkflowRunRecord>(modelBuilder, "workflow_runs");
        Map<WorkflowStepRecord>(modelBuilder, "workflow_steps");

        // 会话按 (tenant_id, user_id) 隔离，两个字段都允许为空（历史数据）。
        modelBuilder.Entity<ConversationRecord>()
            .HasIndex(x => new { x.TenantId, x.UserId });
    }

    private static void Map<TEntity>(ModelBuilder modelBuilder, string table, bool generated = true)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>(e =>
        {
            e.ToTable(table);
            e.HasKey("Id");
            if (generated)
                e.Property("Id").ValueGeneratedOnAdd();
        });
    }
}

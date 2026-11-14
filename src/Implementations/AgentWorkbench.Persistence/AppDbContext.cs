using AgentWorkbench.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Persistence;

/// <summary>
/// EF Core 映射层：只做读写映射，<b>不做建表、不做迁移</b>。
/// </summary>
/// <remarks>
/// <para><b>唯一 schema 真相仍是 FluentMigrator</b>（Migrations/M0xx）。继承
/// <see cref="IdentityDbContext{TUser,TRole,TKey}"/> 只是拿到 Identity 的实体映射，
/// 一旦这里出现 <c>Database.Migrate()</c> 或 <c>EnsureCreated()</c>，就有两套建表来源；
/// 因此本项目<b>不引用</b>任何 EF 迁移包，也没有 EF 的 Migrations 目录。</para>
/// <para><b>Identity 表名显式钉死</b>成 <c>aspnet_*</c>：命名约定本来会把 <c>AspNetUsers</c> 转成
/// <c>aspnet_users</c>，但那是约定推导的结果，改约定就会漂移；这里写死，与 M029 迁移一一对应。</para>
/// <para><b>旧 <c>users</c> 表不再映射</b>：M025 建的自建用户表被 <c>aspnet_users</c> 取代，
/// 表本身先留着（不删数据），由后续迁移确认后再清理。</para>
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole, string>(options)
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
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();

    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();

    public DbSet<WorkflowDefinitionRecord> WorkflowDefinitions => Set<WorkflowDefinitionRecord>();
    public DbSet<PromptVersionRecord> PromptVersions => Set<PromptVersionRecord>();
    public DbSet<WorkflowRunRecord> WorkflowRuns => Set<WorkflowRunRecord>();
    public DbSet<WorkflowStepRecord> WorkflowSteps => Set<WorkflowStepRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Identity 表统一 aspnet_* 前缀，与 M029 迁移逐列对应。
        modelBuilder.Entity<AppUser>().ToTable("aspnet_users");
        modelBuilder.Entity<IdentityRole>().ToTable("aspnet_roles");
        modelBuilder.Entity<IdentityUserRole<string>>().ToTable("aspnet_user_roles");
        modelBuilder.Entity<IdentityUserClaim<string>>().ToTable("aspnet_user_claims");
        modelBuilder.Entity<IdentityRoleClaim<string>>().ToTable("aspnet_role_claims");
        modelBuilder.Entity<IdentityUserLogin<string>>().ToTable("aspnet_user_logins");
        modelBuilder.Entity<IdentityUserToken<string>>().ToTable("aspnet_user_tokens");

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
        Map<ChatMessageRecord>(modelBuilder, "chat_messages");

        Map<TenantRecord>(modelBuilder, "tenants", generated: false);

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

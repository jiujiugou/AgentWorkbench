using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 长期记忆表：给「记住用户偏好 / 决策 / 事实」提供载体（缺口清单 P4-1）。
/// </summary>
/// <remarks>
/// 隔离维度是 <c>(scope_type, scope_id)</c>，覆盖维度是 <c>mem_key</c>：
/// 同 <c>(scope_type, scope_id, mem_key)</c> 的写入走 UPSERT 覆盖，<c>mem_key</c> 为 NULL 时总是新增。
/// SQLite 的唯一索引把多个 NULL 视为互不相同，因此「没有 key 的记忆」不会被去重键误伤。
/// </remarks>
[Migration(202601010024)]
public sealed class M024_CreateMemoryItems : Migration
{
    public override void Up()
    {
        Create.Table("memory_items")
            .WithColumn("id").AsString(32).PrimaryKey()
            .WithColumn("scope_type").AsString(16).NotNullable()
            .WithColumn("scope_id").AsString(64).NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("kind").AsString(32).NotNullable().WithDefaultValue("fact")
            .WithColumn("mem_key").AsString(128).Nullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("importance").AsDouble().NotNullable().WithDefaultValue(0.5)
            .WithColumn("source_session_id").AsString(64).Nullable()
            .WithColumn("source_run_id").AsInt64().Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("updated_at").AsString(32).NotNullable()
            .WithColumn("last_used_at").AsString(32).Nullable()
            .WithColumn("use_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("expires_at").AsString(32).Nullable();

        Create.Index("ux_memory_items_key")
            .OnTable("memory_items")
            .OnColumn("scope_type").Ascending()
            .OnColumn("scope_id").Ascending()
            .OnColumn("mem_key").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_memory_items_scope")
            .OnTable("memory_items")
            .OnColumn("scope_type").Ascending()
            .OnColumn("scope_id").Ascending();

        Create.Index("ix_memory_items_expires")
            .OnTable("memory_items")
            .OnColumn("expires_at").Ascending();
    }

    public override void Down() => Delete.Table("memory_items");
}

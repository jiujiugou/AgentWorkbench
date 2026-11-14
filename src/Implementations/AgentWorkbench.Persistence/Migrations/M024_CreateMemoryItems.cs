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
        // DDL 只读 MemoryItemsSchema.Statements，与测试建库共用同一份。
        foreach (var statement in MemoryItemsSchema.Statements)
            Execute.Sql(statement);
    }

    public override void Down() => Delete.Table("memory_items");
}

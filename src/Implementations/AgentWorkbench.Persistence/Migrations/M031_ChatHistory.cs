using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 对话历史逐条落库（取代「整个会话序列化后覆盖 agent_sessions 一行」）。
/// DDL 只读 <see cref="ChatHistorySchema.Statements"/>，与存储构造共用同一份。
///
/// Down 也留空：历史是「模型当时看到了什么」的唯一证据，回滚版本不应顺手把它删掉。
/// </summary>
[Migration(202601010031)]
public sealed class M031_ChatHistory : Migration
{
    public override void Up()
    {
        foreach (var statement in ChatHistorySchema.Statements)
            Execute.Sql(statement);
    }

    public override void Down()
    {
    }
}

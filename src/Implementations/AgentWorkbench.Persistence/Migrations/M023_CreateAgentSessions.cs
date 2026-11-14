using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 会话快照表：会话序列化后落库，重启后同一 session_id 仍能续上（缺口清单 P1-3）。
/// state_json 是 MAF 的序列化结果（含聊天历史），本表不解释它的结构。
/// expires_at 为空表示不过期；过期行由读路径与后台清理共同回收。
/// </summary>
[Migration(202601010023)]
public sealed class M023_CreateAgentSessions : Migration
{
    public override void Up()
    {
        // DDL 只读 AgentSessionsSchema.Statements，与测试建库共用同一份。
        foreach (var statement in AgentSessionsSchema.Statements)
            Execute.Sql(statement);
    }

    public override void Down() => Delete.Table("agent_sessions");
}

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
        Create.Table("agent_sessions")
            .WithColumn("session_id").AsString(64).PrimaryKey()
            .WithColumn("state_json").AsString(int.MaxValue).NotNullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("updated_at").AsString(32).NotNullable()
            .WithColumn("expires_at").AsString(32).Nullable();

        Create.Index("ix_agent_sessions_expires").OnTable("agent_sessions")
            .OnColumn("expires_at").Ascending();
    }

    public override void Down() => Delete.Table("agent_sessions");
}

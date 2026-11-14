using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// agent_steps 增加 error 列：工具调用失败时记异常类型与消息。
/// 没有它，失败的调用在轨迹里与「返回了空」无法区分（回放时看不出哪一步炸过）。
/// </summary>
[Migration(202601010021)]
public sealed class M021_AddAgentStepError : Migration
{
    public override void Up()
        => Alter.Table("agent_steps").AddColumn("error").AsString(int.MaxValue).Nullable();

    public override void Down()
        => Delete.Column("error").FromTable("agent_steps");
}

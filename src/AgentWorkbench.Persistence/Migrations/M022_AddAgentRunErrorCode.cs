using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// agent_runs 增加 error_code 列：把失败 / 终止归类成机器可判定的码。
/// 只有 error 一串人话时，调用方没法据此决定「重试、降级还是提示用户」；
/// 状态（status）回答「以什么状态结束的」，本列回答「为什么」。
/// </summary>
[Migration(202601010022)]
public sealed class M022_AddAgentRunErrorCode : Migration
{
    public override void Up()
        => Alter.Table("agent_runs").AddColumn("error_code").AsString(32).Nullable();

    public override void Down()
        => Delete.Column("error_code").FromTable("agent_runs");
}

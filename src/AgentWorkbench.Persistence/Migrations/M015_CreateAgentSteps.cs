using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>Agent Harness 轨迹表（每步 kind/tool/arguments/result 留痕）。</summary>
[Migration(202601010015)]
public sealed class M015_CreateAgentSteps : Migration
{
    public override void Up()
    {
        Create.Table("agent_steps")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("agent_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("iteration").AsInt32().NotNullable()
            .WithColumn("kind").AsString(16).NotNullable()
            .WithColumn("tool").AsString(64).Nullable()
            .WithColumn("arguments").AsString(int.MaxValue).Nullable()
            .WithColumn("result").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_agent_steps_run").OnTable("agent_steps").OnColumn("run_id").Ascending();
    }

    public override void Down() => Delete.Table("agent_steps");
}

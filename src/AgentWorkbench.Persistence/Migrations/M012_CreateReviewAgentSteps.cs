using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P3 Agent Loop 轨迹表（每步的工具/参数/结果留痕）。</summary>
[Migration(202601010012)]
public sealed class M012_CreateReviewAgentSteps : Migration
{
    public override void Up()
    {
        Create.Table("review_agent_steps")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("review_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("finding_id").AsInt64().Nullable()
                .ForeignKey("review_findings", "id").OnDelete(Rule.Cascade)
            .WithColumn("step_index").AsInt32().NotNullable()
            .WithColumn("kind").AsString(16).NotNullable()
            .WithColumn("tool").AsString(64).Nullable()
            .WithColumn("arguments").AsString(int.MaxValue).Nullable()
            .WithColumn("result").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_review_agent_steps_run").OnTable("review_agent_steps")
            .OnColumn("run_id").Ascending()
            .OnColumn("finding_id").Ascending();
    }

    public override void Down() => Delete.Table("review_agent_steps");
}

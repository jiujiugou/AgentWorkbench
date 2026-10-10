using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P4 工作流步骤（步骤级状态 = 检查点）。</summary>
[Migration(202601010020)]
public sealed class M020_CreateWorkflowSteps : Migration
{
    public override void Up()
    {
        Create.Table("workflow_steps")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("workflow_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("step_key").AsString(64).NotNullable()
            .WithColumn("step_type").AsString(32).NotNullable()
            .WithColumn("order_index").AsInt32().NotNullable()
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("pending")
            .WithColumn("attempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("input").AsString(int.MaxValue).Nullable()
            .WithColumn("output").AsString(int.MaxValue).Nullable()
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("started_at").AsString(32).Nullable()
            .WithColumn("finished_at").AsString(32).Nullable();

        Create.Index("ix_workflow_steps_run").OnTable("workflow_steps")
            .OnColumn("run_id").Ascending().OnColumn("order_index").Ascending();
    }

    public override void Down() => Delete.Table("workflow_steps");
}

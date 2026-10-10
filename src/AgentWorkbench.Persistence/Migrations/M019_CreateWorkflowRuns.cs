using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P4 工作流运行。</summary>
[Migration(202601010019)]
public sealed class M019_CreateWorkflowRuns : Migration
{
    public override void Up()
    {
        Create.Table("workflow_runs")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("workflow_key").AsString(64).NotNullable()
            .WithColumn("document_id").AsString(32).NotNullable()
                .ForeignKey("documents", "id").OnDelete(Rule.Cascade)
            .WithColumn("prompt_version").AsString(64).Nullable()
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("pending")
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("latency_ms").AsInt64().Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("finished_at").AsString(32).Nullable();

        Create.Index("ix_workflow_runs_document").OnTable("workflow_runs").OnColumn("document_id").Ascending();
    }

    public override void Down() => Delete.Table("workflow_runs");
}

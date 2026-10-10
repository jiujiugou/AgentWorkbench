using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P3 审查结论生成任务表（一次审查 run 对应一个 job）。</summary>
[Migration(202601010013)]
public sealed class M013_CreateAdvisorJobs : Migration
{
    public override void Up()
    {
        Create.Table("advisor_jobs")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("review_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("pending")
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("finished_at").AsString(32).Nullable();

        Create.Index("ix_advisor_jobs_run").OnTable("advisor_jobs").OnColumn("run_id").Ascending().WithOptions().Unique();
    }

    public override void Down() => Delete.Table("advisor_jobs");
}

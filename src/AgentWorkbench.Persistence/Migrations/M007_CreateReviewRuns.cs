using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P2 审查任务表：一次「要素抽取 + 比对」运行。</summary>
[Migration(202601010007)]
public sealed class M007_CreateReviewRuns : Migration
{
    public override void Up()
    {
        Create.Table("review_runs")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("document_id").AsString(36).NotNullable()
                .ForeignKey("documents", "id").OnDelete(Rule.Cascade)
            .WithColumn("mode").AsString(16).NotNullable().WithDefaultValue("both")
            .WithColumn("template").AsString(128).Nullable()
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("pending")
            .WithColumn("steps").AsString(int.MaxValue).NotNullable().WithDefaultValue("[]")
            .WithColumn("provider_id").AsInt64().Nullable()
            .WithColumn("model").AsString(256).Nullable()
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("latency_ms").AsInt64().Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("finished_at").AsString(32).Nullable();

        Create.Index("ix_review_runs_doc").OnTable("review_runs").OnColumn("document_id").Ascending();
    }

    public override void Down() => Delete.Table("review_runs");
}

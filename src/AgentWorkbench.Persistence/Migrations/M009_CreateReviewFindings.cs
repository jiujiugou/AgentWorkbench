using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P2 不一致 / 风险清单表，带严重级与原文证据。</summary>
[Migration(202601010009)]
public sealed class M009_CreateReviewFindings : Migration
{
    public override void Up()
    {
        Create.Table("review_findings")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("review_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("rule_id").AsInt64().Nullable()
                .ForeignKey("rules", "id").OnDelete(Rule.SetNull)
            .WithColumn("severity").AsString(16).NotNullable().WithDefaultValue("medium")
            .WithColumn("category").AsString(64).NotNullable()
            .WithColumn("title").AsString(512).NotNullable()
            .WithColumn("detail").AsString(int.MaxValue).Nullable()
            .WithColumn("expected").AsString(1024).Nullable()
            .WithColumn("actual").AsString(1024).Nullable()
            .WithColumn("chunk_id").AsInt64().Nullable()
                .ForeignKey("chunks", "id").OnDelete(Rule.SetNull)
            .WithColumn("seq").AsInt32().Nullable()
            .WithColumn("page_from").AsInt32().Nullable()
            .WithColumn("page_to").AsInt32().Nullable()
            .WithColumn("evidence").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_review_findings_run").OnTable("review_findings").OnColumn("run_id").Ascending();
    }

    public override void Down() => Delete.Table("review_findings");
}

using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P3 审查结论表，带原文引用与依据状态。</summary>
[Migration(202601010011)]
public sealed class M011_CreateReviewConclusions : Migration
{
    public override void Up()
    {
        Create.Table("review_conclusions")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("review_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("finding_id").AsInt64().Nullable()
                .ForeignKey("review_findings", "id").OnDelete(Rule.Cascade)
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("insufficient")
            .WithColumn("title").AsString(512).NotNullable()
            .WithColumn("conclusion").AsString(int.MaxValue).Nullable()
            .WithColumn("suggestion").AsString(int.MaxValue).Nullable()
            .WithColumn("impact").AsString(int.MaxValue).Nullable()
            .WithColumn("basis").AsString(16).NotNullable().WithDefaultValue("no-evidence")
            .WithColumn("confidence").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("source").AsString(16).NotNullable().WithDefaultValue("llm")
            .WithColumn("chunk_id").AsInt64().Nullable()
                .ForeignKey("chunks", "id").OnDelete(Rule.SetNull)
            .WithColumn("seq").AsInt32().Nullable()
            .WithColumn("page_from").AsInt32().Nullable()
            .WithColumn("page_to").AsInt32().Nullable()
            .WithColumn("evidence").AsString(int.MaxValue).Nullable()
            .WithColumn("citations").AsString(int.MaxValue).Nullable()
            .WithColumn("prompt_version").AsString(32).Nullable()
            .WithColumn("steps_used").AsInt32().Nullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_review_conclusions_run").OnTable("review_conclusions").OnColumn("run_id").Ascending();
    }

    public override void Down() => Delete.Table("review_conclusions");
}

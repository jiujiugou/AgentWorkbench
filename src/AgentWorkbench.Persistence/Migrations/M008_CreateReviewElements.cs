using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P2 要素抽取结果表，每条绑定原文证据块。</summary>
[Migration(202601010008)]
public sealed class M008_CreateReviewElements : Migration
{
    public override void Up()
    {
        Create.Table("review_elements")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsInt64().NotNullable()
                .ForeignKey("review_runs", "id").OnDelete(Rule.Cascade)
            .WithColumn("key").AsString(64).NotNullable()
            .WithColumn("label").AsString(128).NotNullable()
            .WithColumn("value").AsString(1024).NotNullable()
            .WithColumn("value_type").AsString(16).NotNullable().WithDefaultValue("text")
            .WithColumn("unit").AsString(32).Nullable()
            .WithColumn("source").AsString(16).NotNullable().WithDefaultValue("rule")
            .WithColumn("confidence").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("chunk_id").AsInt64().Nullable()
                .ForeignKey("chunks", "id").OnDelete(Rule.SetNull)
            .WithColumn("seq").AsInt32().Nullable()
            .WithColumn("page_from").AsInt32().Nullable()
            .WithColumn("page_to").AsInt32().Nullable()
            .WithColumn("evidence").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_review_elements_run").OnTable("review_elements").OnColumn("run_id").Ascending();
    }

    public override void Down() => Delete.Table("review_elements");
}

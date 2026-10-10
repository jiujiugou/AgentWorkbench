using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

[Migration(202601010003)]
public sealed class M003_CreateChunks : Migration
{
    public override void Up()
    {
        Create.Table("chunks")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("document_id").AsString(36).NotNullable()
                .ForeignKey("documents", "id").OnDelete(Rule.Cascade)
            .WithColumn("seq").AsInt32().NotNullable()
            .WithColumn("page_from").AsInt32().NotNullable()
            .WithColumn("page_to").AsInt32().NotNullable()
            .WithColumn("heading").AsString(256).Nullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("char_count").AsInt32().NotNullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_chunks_doc").OnTable("chunks").OnColumn("document_id").Ascending();
    }

    public override void Down() => Delete.Table("chunks");
}

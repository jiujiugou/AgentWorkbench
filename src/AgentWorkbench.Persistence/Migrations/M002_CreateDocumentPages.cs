using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

[Migration(202601010002)]
public sealed class M002_CreateDocumentPages : Migration
{
    public override void Up()
    {
        Create.Table("document_pages")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("document_id").AsString(36).NotNullable()
                .ForeignKey("documents", "id").OnDelete(Rule.Cascade)
            .WithColumn("page_no").AsInt32().NotNullable()
            .WithColumn("raw_text").AsString(int.MaxValue).NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("cleaned_text").AsString(int.MaxValue).NotNullable().WithDefaultValue(string.Empty);

        Create.Index("ix_document_pages_doc").OnTable("document_pages").OnColumn("document_id").Ascending();
    }

    public override void Down() => Delete.Table("document_pages");
}

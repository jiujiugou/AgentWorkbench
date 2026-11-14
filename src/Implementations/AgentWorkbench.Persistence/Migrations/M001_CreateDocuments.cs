using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

[Migration(202601010001)]
public sealed class M001_CreateDocuments : Migration
{
    public override void Up()
    {
        Create.Table("documents")
            .WithColumn("id").AsString(36).PrimaryKey()
            .WithColumn("file_name").AsString(512).NotNullable()
            .WithColumn("stored_path").AsString(1024).NotNullable()
            .WithColumn("sha256").AsString(64).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("status").AsString(16).NotNullable()
            .WithColumn("page_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("parsed_at").AsString(32).Nullable();

        Create.Index("ix_documents_sha256").OnTable("documents").OnColumn("sha256").Ascending();
    }

    public override void Down() => Delete.Table("documents");
}

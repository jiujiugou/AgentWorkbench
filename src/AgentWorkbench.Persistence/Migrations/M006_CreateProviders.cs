using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 模型供应商 + 其下可选模型（运行时增删/切换，无需重启）。
/// API Key 先明文落库（P2 再加密）；is_active 标记当前供应商，is_default 标记该供应商当前模型。
/// </summary>
[Migration(202601010006)]
public sealed class M006_CreateProviders : Migration
{
    public override void Up()
    {
        Create.Table("providers")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("name").AsString(128).NotNullable()
            .WithColumn("base_url").AsString(512).NotNullable()
            .WithColumn("api_key").AsString(1024).NotNullable().WithDefaultValue("")
            .WithColumn("is_active").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("updated_at").AsString(32).NotNullable();

        Create.Table("provider_models")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("provider_id").AsInt64().NotNullable()
                .ForeignKey("providers", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("model").AsString(256).NotNullable()
            .WithColumn("display_name").AsString(256).Nullable()
            .WithColumn("is_default").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_provider_models_provider").OnTable("provider_models").OnColumn("provider_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("provider_models");
        Delete.Table("providers");
    }
}

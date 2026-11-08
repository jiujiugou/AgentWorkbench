using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P4 提示词版本化。</summary>
[Migration(202601010018)]
public sealed class M018_CreatePromptVersions : Migration
{
    public override void Up()
    {
        Create.Table("prompt_versions")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("prompt_key").AsString(64).NotNullable()
            .WithColumn("version").AsString(64).NotNullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("active").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ux_prompt_versions_key_version").OnTable("prompt_versions")
            .OnColumn("prompt_key").Ascending().OnColumn("version").Ascending().WithOptions().Unique();
    }

    public override void Down() => Delete.Table("prompt_versions");
}

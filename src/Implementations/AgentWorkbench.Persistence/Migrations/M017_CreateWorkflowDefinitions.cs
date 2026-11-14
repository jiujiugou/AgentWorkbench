using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P4 工作流定义。</summary>
[Migration(202601010017)]
public sealed class M017_CreateWorkflowDefinitions : Migration
{
    public override void Up()
    {
        Create.Table("workflow_definitions")
            .WithColumn("id").AsString(32).PrimaryKey()
            .WithColumn("key").AsString(64).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("definition").AsString(int.MaxValue).NotNullable()
            .WithColumn("enabled").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("updated_at").AsString(32).NotNullable();

        Create.Index("ux_workflow_definitions_key").OnTable("workflow_definitions").OnColumn("key").Ascending().WithOptions().Unique();
    }

    public override void Down() => Delete.Table("workflow_definitions");
}

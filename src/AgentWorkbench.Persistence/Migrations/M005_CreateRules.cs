using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>规则库表（标准条款 / 风险清单）：P1 只建表 + 管理入口，内容 P2 填充。</summary>
[Migration(202601010005)]
public sealed class M005_CreateRules : Migration
{
    public override void Up()
    {
        Create.Table("rules")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("category").AsString(128).NotNullable()
            .WithColumn("name").AsString(512).NotNullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("version").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_rules_category").OnTable("rules").OnColumn("category").Ascending();
    }

    public override void Down() => Delete.Table("rules");
}

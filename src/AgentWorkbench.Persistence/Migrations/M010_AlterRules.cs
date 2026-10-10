using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>P2 扩展规则库：规则类型 / 严重级 / 目标要素 / 期望值 / 匹配关键词 / 启用位。</summary>
[Migration(202601010010)]
public sealed class M010_AlterRules : Migration
{
    public override void Up()
    {
        Alter.Table("rules")
            .AddColumn("rule_type").AsString(32).NotNullable().WithDefaultValue("clause_required")
            .AddColumn("severity").AsString(16).NotNullable().WithDefaultValue("medium")
            .AddColumn("target_key").AsString(64).Nullable()
            .AddColumn("expected").AsString(512).Nullable()
            .AddColumn("match_hint").AsString(512).Nullable()
            .AddColumn("enabled").AsInt32().NotNullable().WithDefaultValue(1);
    }

    public override void Down()
    {
        Delete.Column("rule_type").FromTable("rules");
        Delete.Column("severity").FromTable("rules");
        Delete.Column("target_key").FromTable("rules");
        Delete.Column("expected").FromTable("rules");
        Delete.Column("match_hint").FromTable("rules");
        Delete.Column("enabled").FromTable("rules");
    }
}

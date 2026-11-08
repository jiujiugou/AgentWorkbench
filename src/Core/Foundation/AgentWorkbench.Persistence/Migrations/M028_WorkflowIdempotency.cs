using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// P0 准入：动作幂等登记。恢复放行改由 MAF checkpoint 决定后，重放会重新投递
/// pending events，必须有一个跨重放的去重表，否则外部写会产生第二次副作用。
/// </summary>
[Migration(202601010028)]
public sealed class M028_WorkflowIdempotency : Migration
{
    public override void Up()
    {
        Alter.Table("workflow_steps")
            .AddColumn("idempotency_key").AsString(128).Nullable();
        Alter.Table("workflow_steps")
            .AddColumn("approval_id").AsString(64).Nullable();
        Alter.Table("workflow_steps")
            .AddColumn("external_ref").AsString(256).Nullable();

        Create.Table("workflow_idempotency")
            .WithColumn("key").AsString(256).PrimaryKey().NotNullable()
            .WithColumn("state").AsString(16).NotNullable().WithDefaultValue("in_flight")
            .WithColumn("output").AsString(int.MaxValue).Nullable()
            .WithColumn("external_ref").AsString(256).Nullable()
            .WithColumn("reason").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("finished_at").AsString(32).Nullable();
    }

    public override void Down()
    {
        Delete.Table("workflow_idempotency");
        Delete.Column("external_ref").FromTable("workflow_steps");
        Delete.Column("approval_id").FromTable("workflow_steps");
        Delete.Column("idempotency_key").FromTable("workflow_steps");
    }
}

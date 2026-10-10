using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>Agent Harness 运行表（横切运行时轴：一次 run 一行）。</summary>
[Migration(202601010014)]
public sealed class M014_CreateAgentRuns : Migration
{
    public override void Up()
    {
        Create.Table("agent_runs")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("session_id").AsString(64).NotNullable()
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("succeeded")
            .WithColumn("iterations").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("model").AsString(128).Nullable()
            .WithColumn("latency_ms").AsInt64().Nullable()
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("finished_at").AsString(32).Nullable();

        Create.Index("ix_agent_runs_session").OnTable("agent_runs").OnColumn("session_id").Ascending();
    }

    public override void Down() => Delete.Table("agent_runs");
}

using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 轨迹补四类事实：用量、完整性、结构化停因、截断标志。
/// </summary>
/// <remarks>
/// 这四类都是「内核产出了、但落不下来」的字段——记录形状上先有了，表里没有，
/// 于是回放读回来全是空。**空字段比没有字段更糟**：它看起来是完整的记录。
/// <list type="bullet">
/// <item><c>input_tokens</c> / <c>output_tokens</c>：本次调用上报的用量；NULL = 供应商没报，与 0 区分。</item>
/// <item><c>complete</c>：这条记录是否覆盖了本次 run 的全部动作；中断（超时 / 取消）为 false。</item>
/// <item><c>stop_kind</c> / <c>stop_observed</c> / <c>stop_limit</c> / <c>stop_subject</c>：
/// 停因的依据（哪个判定触发、实际值、阈值、对象）。只有一句中文时，机器读不到「为什么停」。</item>
/// <item><c>truncated</c>：该步结果是否被截断。静默截断会让「返回了这么多」与「实际返回更多」无法区分。</item>
/// </list>
/// <para>
/// <c>complete</c> 与 <c>truncated</c> 都带默认值：SQLite 的 ALTER TABLE ADD COLUMN
/// 对 NOT NULL 列必须有默认值，且历史行需要被赋予「合理的历史语义」——老记录确实写全了，
/// 所以默认 true；老步骤确实没截断记录，默认 false。
/// </para>
/// </remarks>
[Migration(202601010033)]
public sealed class M033_AddTraceFacts : Migration
{
    public override void Up()
    {
        Alter.Table("agent_runs")
            .AddColumn("input_tokens").AsInt64().Nullable()
            .AddColumn("output_tokens").AsInt64().Nullable()
            .AddColumn("complete").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("stop_kind").AsString(64).Nullable()
            .AddColumn("stop_observed").AsInt64().Nullable()
            .AddColumn("stop_limit").AsInt64().Nullable()
            .AddColumn("stop_subject").AsString(256).Nullable();

        Alter.Table("agent_steps")
            .AddColumn("truncated").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("input_tokens").FromTable("agent_runs");
        Delete.Column("output_tokens").FromTable("agent_runs");
        Delete.Column("complete").FromTable("agent_runs");
        Delete.Column("stop_kind").FromTable("agent_runs");
        Delete.Column("stop_observed").FromTable("agent_runs");
        Delete.Column("stop_limit").FromTable("agent_runs");
        Delete.Column("stop_subject").FromTable("agent_runs");
        Delete.Column("truncated").FromTable("agent_steps");
    }
}

using AgentWorkbench.Persistence.ActionStore;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 动作 run 状态、幂等登记、终态与审计轨迹落库（M2 §1.1 那项）。
/// DDL 只读 <see cref="ActionStoreSchema.Statements"/>，与 SQLite 存储构造共用同一份，避免漂移。
///
/// Down 刻意留空：审计轨迹是「办错了能追回」的唯一证据，回滚版本不应顺手把证据删掉；
/// 真要清理需人工决策，不走自动迁移。
/// </summary>
[Migration(202601010030)]
public sealed class M030_ActionRunStore : Migration
{
    public override void Up()
    {
        foreach (var statement in ActionStoreSchema.Statements)
            Execute.Sql(statement);
    }

    public override void Down()
    {
    }
}

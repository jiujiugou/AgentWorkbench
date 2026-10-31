using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 清理已废弃的「合同审查」垂直模块残留（7 张表，删除时共 77 行）。
/// </summary>
/// <remarks>
/// 为什么需要一个「只做删除」的迁移：这 7 张表的创建者 M005 / M007–M013 已随模块砍伐删除，
/// 代码里 0 引用——但 <c>VersionInfo</c> 仍记着它们，老库里也还有表和数据。
/// 新库本来就没有这些表，老库才需要删。所以迁移必须对**两种起点**都成立，
/// 全部用 <c>DROP TABLE IF EXISTS</c>。
///
/// 删除顺序：子表 → 父表。外键链是
/// <c>review_runs → review_elements / review_findings / review_conclusions / review_agent_steps / advisor_jobs</c>，
/// 且 <c>review_findings → review_conclusions</c>、<c>advisor_jobs → review_runs</c>。
/// 连接上 <c>PRAGMA foreign_keys=ON</c> 时，DROP 父表会级联删子表行——
/// 先删子表能把「删了哪张、删了几行」说清楚，而不是等一次级联悄悄带走。
///
/// 没有加行数守卫（与之相对，M027 删 <c>users</c> 加了）：这 77 行已确认无业务价值，
/// 且删除前已用 <c>sqlite3.backup()</c> 整库备份，回滚靠备份而不是靠迁移。
/// </remarks>
[Migration(202601010026)]
public sealed class M026_DropOrphanTables : Migration
{
    private static readonly string[] Tables =
    [
        "advisor_jobs",
        "review_conclusions",
        "review_agent_steps",
        "review_elements",
        "review_findings",
        "review_runs",
        "rules",
    ];

    public override void Up()
    {
        foreach (var table in Tables)
            Execute.Sql($"DROP TABLE IF EXISTS \"{table}\"");
    }

    /// <summary>
    /// 空实现：被删的表与数据都不可重建。
    /// </summary>
    /// <remarks>
    /// 故意不在这里重建空表——重建只会得到一个「看起来回来了、实际没有数据」的假象，
    /// 比明确失败更危险。需要恢复请用删除前的库备份（见 docs/设计/ 下的表结构文档 §19）。
    /// </remarks>
    public override void Down()
    {
    }
}

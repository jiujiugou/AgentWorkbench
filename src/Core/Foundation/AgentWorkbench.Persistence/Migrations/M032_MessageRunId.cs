using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 给 <c>messages</c> 加 <c>run_id</c>：assistant 消息指向产生它的那次 run。
///
/// 界面回看历史时靠它找回执行过程（工具调用 / 迭代数 / 终态）。
/// 此前这条链只靠「实时推送」活着，一关页面就没了——历史里永远显示"没有过程"。
///
/// 可空：user 消息没有 run，历史数据也补不出来（当时没记）。补不出来的就留 null，
/// <b>不按时间戳猜</b>——猜错会把别人的过程挂到这条消息上，比空着更糟。
///
/// 不加索引：没有任何地方按 run_id 查 messages，方向是反的（消息 → run）。
/// </summary>
[Migration(202601010032)]
public sealed class M032_MessageRunId : Migration
{
    public override void Up()
    {
        Alter.Table("messages")
            .AddColumn("run_id").AsInt64().Nullable();
    }

    public override void Down()
    {
        // 留空：这一列是既有历史的补充证据，回滚版本不该顺手抹掉已记下的关联。
    }
}

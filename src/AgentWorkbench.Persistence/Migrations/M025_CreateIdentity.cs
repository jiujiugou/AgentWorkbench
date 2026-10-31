using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 多租户的最底层：租户目录 + 用户表 + 会话归属列。
/// </summary>
/// <remarks>
/// 为什么先落这两张表：多租户 = 「谁」+ 隔离，而本仓此前**零身份**（无 Authorize、无 ClaimsPrincipal，
/// 会话 id 由客户端传）。没有「谁」，给业务表加 tenant_id 只会得到一列永远为 NULL。
/// 所以第一刀只做「谁」，并让它在一个用户最直观的地方生效：会话列表。
///
/// 隔离范围（阶段 0.5，诚实的边界）：
/// - 已隔离：conversations（含消息，经会话间接隔离）
/// - 未隔离：documents / chunks / agent_runs / memory_items / workflow（下一阶段，见 docs/跨模块记录/多租户方案.md）
///
/// 归属列允许 NULL：迁移前的历史数据没有主人，归入「迁移前的公共数据」，
/// 由 <c>tenant_id IS NULL</c> 判定；不给旧行瞎猜归属。
/// </remarks>
[Migration(202601010025)]
public sealed class M025_CreateIdentity : Migration
{
    public override void Up()
    {
        Create.Table("tenants")
            .WithColumn("id").AsString(32).PrimaryKey()
            .WithColumn("name").AsString(128).NotNullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ux_tenants_name").OnTable("tenants").OnColumn("name").Ascending().WithOptions().Unique();

        Create.Table("users")
            .WithColumn("id").AsString(32).PrimaryKey()
            .WithColumn("tenant_id").AsString(32).NotNullable()
                .ForeignKey("tenants", "id")
            .WithColumn("user_name").AsString(64).NotNullable()
            .WithColumn("display_name").AsString(64).NotNullable()
            .WithColumn("password_hash").AsString(256).NotNullable()
            .WithColumn("role").AsString(32).NotNullable().WithDefaultValue("member")
            .WithColumn("is_active").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("created_at").AsString(32).NotNullable();

        // 登录靠它定位用户（全局唯一，见 UserRecord 注释）
        Create.Index("ux_users_user_name").OnTable("users").OnColumn("user_name").Ascending().WithOptions().Unique();
        Create.Index("ix_users_tenant").OnTable("users").OnColumn("tenant_id").Ascending();

        Alter.Table("conversations")
            .AddColumn("tenant_id").AsString(32).Nullable()
            .AddColumn("user_id").AsString(32).Nullable();

        Create.Index("ix_conversations_owner")
            .OnTable("conversations")
            .OnColumn("tenant_id").Ascending()
            .OnColumn("user_id").Ascending()
            .OnColumn("updated_at").Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_conversations_owner").OnTable("conversations");
        Delete.Column("tenant_id").FromTable("conversations");
        Delete.Column("user_id").FromTable("conversations");
        Delete.Table("users");
        Delete.Table("tenants");
    }
}

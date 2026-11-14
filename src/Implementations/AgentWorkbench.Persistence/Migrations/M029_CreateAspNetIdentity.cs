using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// 用 ASP.NET Core Identity 的标准表结构取代 M025 的自建 <c>users</c> 表。
/// </summary>
/// <remarks>
/// <para><b>为什么建全套 7 张表</b>：走的是 <c>IdentityDbContext&lt;AppUser, IdentityRole, string&gt;</c> +
/// <c>AddEntityFrameworkStores</c> 的标准套路，框架的 store 会同时读写用户、角色、声明、登录、令牌五类数据。
/// 少建一张，登录能过但 <c>AddToRoleAsync</c> / <c>GetRolesAsync</c> 会在运行时炸。</para>
/// <para><b>列名与 EF 模型逐列对齐</b>：表名、列名都按 <c>UseSnakeCaseNamingConvention()</c> 的结果写死，
/// 与 <c>AppDbContext.OnModelCreating</c> 里的 <c>ToTable("aspnet_*")</c> 一一对应。
/// 外键全部 Cascade：删用户时其角色/声明/登录/令牌随之清理。</para>
/// <para><b>旧 <c>users</c> 表保留不删</b>：M025 建的那张表连同数据先留着，等确认无回滚需求再单独清理。
/// 本迁移只做「新增」，对已有库与全新库都成立。</para>
/// </remarks>
[Migration(202601010029)]
public sealed class M029_CreateAspNetIdentity : Migration
{
    public override void Up()
    {
        Create.Table("aspnet_roles")
            .WithColumn("id").AsString(64).PrimaryKey()
            .WithColumn("name").AsString(256).Nullable()
            .WithColumn("normalized_name").AsString(256).Nullable()
            .WithColumn("concurrency_stamp").AsString(256).Nullable();

        Create.Index("ux_aspnet_roles_normalized_name")
            .OnTable("aspnet_roles").OnColumn("normalized_name").Ascending().WithOptions().Unique();

        Create.Table("aspnet_users")
            .WithColumn("id").AsString(64).PrimaryKey()
            .WithColumn("user_name").AsString(256).Nullable()
            .WithColumn("normalized_user_name").AsString(256).Nullable()
            .WithColumn("email").AsString(256).Nullable()
            .WithColumn("normalized_email").AsString(256).Nullable()
            .WithColumn("email_confirmed").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("password_hash").AsString(1024).Nullable()
            .WithColumn("security_stamp").AsString(256).Nullable()
            .WithColumn("concurrency_stamp").AsString(256).Nullable()
            .WithColumn("phone_number").AsString(64).Nullable()
            .WithColumn("phone_number_confirmed").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("two_factor_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            // DateTimeOffset? 在 SQLite 下由 EF 存成 ISO8601 文本，这里用 TEXT 对齐。
            .WithColumn("lockout_end").AsCustom("TEXT").Nullable()
            .WithColumn("lockout_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("access_failed_count").AsInt32().NotNullable().WithDefaultValue(0)
            // 业务侧扩展列
            .WithColumn("tenant_id").AsString(32).NotNullable()
            .WithColumn("display_name").AsString(64).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ux_aspnet_users_normalized_user_name")
            .OnTable("aspnet_users").OnColumn("normalized_user_name").Ascending().WithOptions().Unique();
        Create.Index("ix_aspnet_users_tenant")
            .OnTable("aspnet_users").OnColumn("tenant_id").Ascending();

        // 复合主键必须写在 CREATE TABLE 里：SQLite 不支持建表后再 ADD CONSTRAINT PRIMARY KEY。
        Execute.Sql("""
            CREATE TABLE "aspnet_user_roles" (
                "user_id" TEXT NOT NULL,
                "role_id" TEXT NOT NULL,
                PRIMARY KEY ("user_id", "role_id"),
                FOREIGN KEY ("user_id") REFERENCES "aspnet_users" ("id") ON DELETE CASCADE,
                FOREIGN KEY ("role_id") REFERENCES "aspnet_roles" ("id") ON DELETE CASCADE
            );
            """);

        Create.Table("aspnet_user_claims")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("user_id").AsString(64).NotNullable()
                .ForeignKey("aspnet_users", "id").OnDelete(Rule.Cascade)
            .WithColumn("claim_type").AsString(256).Nullable()
            .WithColumn("claim_value").AsString(1024).Nullable();

        Create.Table("aspnet_role_claims")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("role_id").AsString(64).NotNullable()
                .ForeignKey("aspnet_roles", "id").OnDelete(Rule.Cascade)
            .WithColumn("claim_type").AsString(256).Nullable()
            .WithColumn("claim_value").AsString(1024).Nullable();

        Execute.Sql("""
            CREATE TABLE "aspnet_user_logins" (
                "login_provider" TEXT NOT NULL,
                "provider_key" TEXT NOT NULL,
                "provider_display_name" TEXT NULL,
                "user_id" TEXT NOT NULL,
                PRIMARY KEY ("login_provider", "provider_key"),
                FOREIGN KEY ("user_id") REFERENCES "aspnet_users" ("id") ON DELETE CASCADE
            );
            """);

        Execute.Sql("""
            CREATE TABLE "aspnet_user_tokens" (
                "user_id" TEXT NOT NULL,
                "login_provider" TEXT NOT NULL,
                "name" TEXT NOT NULL,
                "value" TEXT NULL,
                PRIMARY KEY ("user_id", "login_provider", "name"),
                FOREIGN KEY ("user_id") REFERENCES "aspnet_users" ("id") ON DELETE CASCADE
            );
            """);
    }

    public override void Down()
    {
        Delete.Table("aspnet_user_tokens");
        Delete.Table("aspnet_user_logins");
        Delete.Table("aspnet_role_claims");
        Delete.Table("aspnet_user_claims");
        Delete.Table("aspnet_user_roles");
        Delete.Table("aspnet_users");
        Delete.Table("aspnet_roles");
    }
}

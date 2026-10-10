using System.Data;
using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>通用聊天会话与消息（上下文持久化：刷新/切换后可恢复）。</summary>
[Migration(202601010016)]
public sealed class M016_CreateConversations : Migration
{
    public override void Up()
    {
        Create.Table("conversations")
            .WithColumn("id").AsString(32).PrimaryKey()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("created_at").AsString(32).NotNullable()
            .WithColumn("updated_at").AsString(32).NotNullable();

        Create.Table("messages")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("conversation_id").AsString(32).NotNullable()
                .ForeignKey("conversations", "id").OnDelete(Rule.Cascade)
            .WithColumn("role").AsString(16).NotNullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("created_at").AsString(32).NotNullable();

        Create.Index("ix_messages_conversation").OnTable("messages")
            .OnColumn("conversation_id").Ascending()
            .OnColumn("id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("messages");
        Delete.Table("conversations");
    }
}

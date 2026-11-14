using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>全文索引表：FTS5 + trigram 分词（对中文子串友好），供 BM25 检索。</summary>
[Migration(202601010004)]
public sealed class M004_CreateChunksFts : Migration
{
    public override void Up()
    {
        Execute.Sql(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                content,
                document_id UNINDEXED,
                chunk_id UNINDEXED,
                tokenize = 'trigram'
            );
            """);
    }

    public override void Down() => Execute.Sql("DROP TABLE IF EXISTS chunks_fts;");
}

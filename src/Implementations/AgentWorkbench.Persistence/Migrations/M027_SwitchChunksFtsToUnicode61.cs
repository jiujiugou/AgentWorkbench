using FluentMigrator;

namespace AgentWorkbench.Persistence.Migrations;

/// <summary>
/// <c>chunks_fts</c> 的 tokenizer 由 <c>trigram</c> 换成 <c>unicode61</c>。
/// </summary>
/// <remarks>
/// <para>
/// M004 当时选 trigram 的理由是「对中文子串友好」，实测不成立：trigram 需要 ≥3 字符，
/// 而中文词多为 1–2 字，正文里的「报警」「串口」根本进不了索引；
/// 更严重的是查询侧按空白/标点切分，中文整句（<c>汇川H5U的故障代码有哪些</c>）
/// 既不 MATCH 也不 LIKE，等于全文那一路完全空转。
/// </para>
/// <para>
/// 改为 unicode61 后，token 边界由写入方决定：应用层先用 bigram 切好、以空格连接写入，
/// 查询侧调同一个 <c>BigramTokenizer</c>。索引内容因此不再是原文，是本表的一次格式变更。
/// </para>
/// <para>
/// <b>数据不迁移。</b>FTS5 的 <c>tokenize</c> 只能在建表时指定，无法 ALTER，只能 DROP + CREATE。
/// 正文真源在 <c>chunks</c> 表，重建后对每篇文档重跑一次索引即可（<c>POST /api/knowledge/documents/{id}/index</c>）。
/// 重建完成前检索会返回空——这是显式的，不是静默降级。
/// </para>
/// </remarks>
[Migration(202601010027)]
public sealed class M027_SwitchChunksFtsToUnicode61 : Migration
{
    public override void Up()
    {
        Execute.Sql("DROP TABLE IF EXISTS chunks_fts;");
        Execute.Sql(
            """
            CREATE VIRTUAL TABLE chunks_fts USING fts5(
                content,
                document_id UNINDEXED,
                chunk_id UNINDEXED,
                tokenize = 'unicode61'
            );
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP TABLE IF EXISTS chunks_fts;");
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
}

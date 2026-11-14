namespace AgentWorkbench.Shared;

/// <summary>文档状态机。</summary>
/// <remarks>
/// 四个状态里没有任何一个会「禁止」某个操作，这是刻意的：没有「必须先停用才能删」之类的前置条件。
/// <list type="bullet">
/// <item>读 pages / chunks / 下载原件：任意状态（无内容返回空数组；文件缺失才是 404）。</item>
/// <item>上传同一内容：当前不去重（未按 sha256 查已有记录，每次新建记录，只有磁盘文件按哈希复用）。</item>
/// <item>重处理：任意状态。<c>parsing</c> 中调用会发生什么尚未决定，不要依赖任何特定行为。</item>
/// <item>删除：任意状态，含 <c>parsing</c> 中。处理中被删，Worker 写库会因记录不存在而失败，属正常竞态，不得重建该文档。</item>
/// </list>
/// <para>
/// <b>为什么放 Shared 而不是持久化实体里</b>：它是跨边界的词表——知识契约（<c>IKnowledgeEngine</c>）
/// 判覆盖度要读它，数据库列存的也是同一组字符串。放在实体项目里会让「契约」编译期依赖「表结构」，
/// 于是底座拖进了存储实现。
/// </para>
/// </remarks>
public static class DocumentStatus
{
    public const string Pending = "pending";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string Failed = "failed";
}

namespace AgentWorkbench.Domain;

/// <summary>文档状态机。</summary>
/// <remarks>
/// 四个状态里没有任何一个会「禁止」某个操作，这是刻意的：没有「必须先停用才能删」之类的前置条件。
/// <list type="bullet">
/// <item>读 pages / chunks / 下载原件：任意状态（无内容返回空数组；文件缺失才是 404）。</item>
/// <item>上传同一内容：**当前不去重**（未按 sha256 查已有记录，每次新建记录，只有磁盘文件按哈希复用）。</item>
/// <item>重处理：任意状态。<c>parsing</c> 中调用会发生什么**尚未决定**，不要依赖任何特定行为。</item>
/// <item>删除：任意状态，含 <c>parsing</c> 中。处理中被删，Worker 写库会因记录不存在而失败，属正常竞态，不得重建该文档。</item>
/// </list>
/// 现状（含未落地项）：docs/现状/知识资料管理.md。
/// </remarks>
public static class DocumentStatus
{
    public const string Pending = "pending";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string Failed = "failed";
}

public sealed class DocumentRecord
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Status { get; set; } = DocumentStatus.Pending;
    public int PageCount { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? ParsedAt { get; set; }
}

public sealed class DocumentPageRecord
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int PageNo { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string CleanedText { get; set; } = string.Empty;
}

public sealed class ChunkRecord
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public int PageFrom { get; set; }
    public int PageTo { get; set; }
    public string? Heading { get; set; }
    public string Content { get; set; } = string.Empty;
    public int CharCount { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

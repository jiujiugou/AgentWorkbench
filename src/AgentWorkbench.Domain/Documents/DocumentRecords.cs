namespace AgentWorkbench.Domain;

/// <summary>文档状态机。</summary>
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

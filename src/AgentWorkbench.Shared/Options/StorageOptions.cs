namespace AgentWorkbench.Shared.Options;

/// <summary>本地存储与上传限制配置。</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>数据根目录（相对内容根，或绝对路径）</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>上传文件目录</summary>
    public string UploadsDir { get; set; } = "data/uploads";

    /// <summary>SQLite 数据库文件路径</summary>
    public string DbPath { get; set; } = "data/agentworkbench.db";

    /// <summary>单文件大小上限（字节）</summary>
    public long MaxFileBytes { get; set; } = 30 * 1024 * 1024;

    /// <summary>单文档页数上限</summary>
    public int MaxPages { get; set; } = 200;
}

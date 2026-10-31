namespace AgentWorkbench.Domain;

/// <summary>
/// 文档接口返回给客户端的业务码（HTTP 响应体里的 <c>code</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么要有码而不是只给 message</b>：<c>message</c> 是给人看的，会改文案、会翻译；
/// 客户端要据此决定「提示重选文件」还是「提示换格式」还是「提示压缩后重传」。
/// 靠匹配中文串来分支，改一个字就断。</para>
/// <para><b>与 <c>EmbeddingFailureCode</c> 的区别</b>：那组是 lower_snake 的内部诊断码，
/// 只在日志与 <c>detail</c> 字段里出现，不承诺稳定；本组是 <b>UPPER_SNAKE 的对外契约</b>，
/// 一旦发布就不能改名（客户端会 switch 它）。两组风格不同是有意的：
/// 看到大小写就能判断「这个码能不能拿来写分支」。</para>
/// <para>新增码只加不改：删一个码等于让已发布的客户端走进 default 分支。</para>
/// </remarks>
public static class DocumentErrorCodes
{
    /// <summary>请求里根本没有 file 字段（表单构造错误，不是用户操作问题）。</summary>
    public const string NoFile = "NO_FILE";

    /// <summary>file 字段存在但长度为 0。</summary>
    public const string FileEmpty = "FILE_EMPTY";

    /// <summary>扩展名不在支持列表内（.pdf / .txt / .md / .json）。</summary>
    public const string FileTypeUnsupported = "FILE_TYPE_UNSUPPORTED";

    /// <summary>超过业务上限（<c>StorageOptions.MaxFileBytes</c>，默认 30 MB）。</summary>
    /// <remarks>与框架的 413 不同：那一条由 Kestrel 请求体上限（当前 64 MB）拦截，没有响应体，也没有码。</remarks>
    public const string FileTooLarge = "FILE_TOO_LARGE";
}

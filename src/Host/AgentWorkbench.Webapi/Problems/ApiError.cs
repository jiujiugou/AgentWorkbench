namespace AgentWorkbench.Webapi.Problems;

/// <summary>
/// HTTP 错误边界的输入描述：纯数据，不引用 ASP.NET、不引用任何业务模块。
///
/// 它只回答「这次失败对外怎么说」：状态码、对外业务码（可为空）、给人看的那句话、
/// 附加细节、能不能重试。「什么算失败」「码叫什么」由控制器与各业务模块决定，
/// 不收进这里——收进来就变成通用载体反向认识业务。
/// </summary>
/// <param name="Status">HTTP 状态码。</param>
/// <param name="Code">对外业务码，沿用各模块自己的常量；没有对外码时为 null。</param>
/// <param name="Message">给人看的那句话，前端三份解析只读它。</param>
/// <param name="Details">附加细节，恒为 null（ADR 0018 #5 保留五字段）。</param>
/// <param name="Retryable">客户端可否重试，默认否。</param>
public sealed record ApiError(
    int Status,
    string? Code,
    string Message,
    string? Details = null,
    bool Retryable = false)
{
    /// <summary>
    /// 该错误独有的额外字段（例：409 删除中断时残留的索引行数）。
    /// 这些字段属于已发布的响应契约，迁移时不能丢；载体只做合并，不解释键名。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Extra { get; init; }
}

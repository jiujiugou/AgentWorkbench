namespace AgentWorkbench.Knowledge.Abstractions.Embedding;

/// <summary>失败的可处置性：决定「重试有没有意义」，不决定「业务链要不要继续」。</summary>
/// <remarks>
/// 后者只有调用边界（索引写入 / 检索 / 健康检查）有资格判断：
/// 同一类失败，在索引写入场景必须让文档转 failed，在检索场景却应降级为纯全文。
/// 分类器只回答前者，因此它是纯函数、无 IO、可穷举测试。
/// </remarks>
public enum FailureKind
{
    /// <summary>不是失败（调用方主动取消）。原样冒泡，不算异常路径。</summary>
    NotFailure,

    /// <summary>暂时性：连接、超时、限流、上游 5xx。重试可能好。</summary>
    Transient,

    /// <summary>永久性：配置错、鉴权失败、契约破坏（维度/条数不符）。重试只会白等。</summary>
    Permanent
}

/// <summary>
/// Embedding 失败的归类码（仿 <c>AgentRunCode</c>：只有一串消息时，调用方无法决定重试还是降级）。
/// </summary>
public static class EmbeddingFailureCode
{
    /// <summary>服务不可达（连接拒绝、DNS、socket 错误、上游 5xx）。</summary>
    public const string Unavailable = "embedding_unavailable";

    /// <summary>超时（单次尝试或总预算耗尽）。</summary>
    public const string Timeout = "embedding_timeout";

    /// <summary>被限流（429）。</summary>
    public const string RateLimited = "embedding_rate_limited";

    /// <summary>鉴权失败（401/403）：key 错或过期。</summary>
    public const string Unauthorized = "embedding_unauthorized";

    /// <summary>模型不存在（404）：模型名写错或未开通。</summary>
    public const string ModelNotFound = "embedding_model_not_found";

    /// <summary>请求被拒（400）：参数或模型名不被接受。</summary>
    public const string BadRequest = "embedding_bad_request";

    /// <summary>响应无法解析（JSON 结构不符）。</summary>
    public const string BadResponse = "embedding_bad_response";

    /// <summary>返回维度与配置的 <c>Dimensions</c> 不符：继续写会污染向量库。</summary>
    public const string DimensionMismatch = "embedding_dimension_mismatch";

    /// <summary>返回条数与输入条数不符：无法与 chunk 一一对应。</summary>
    public const string CountMismatch = "embedding_count_mismatch";

    /// <summary>未配置（Provider = none 或缺少 ApiKey/Model）。</summary>
    public const string NotConfigured = "embedding_not_configured";

    /// <summary>向量索引不可用（扩展未加载或集合未建立），写入/删除都无法进行。</summary>
    public const string VectorIndexUnavailable = "vector_index_unavailable";

    /// <summary>删除向量失败：索引里会留下指向已删分块的残留，检索时会占用候选名额。</summary>
    public const string VectorDeleteFailed = "vector_delete_failed";

    /// <summary>调用方主动取消。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>未归类的异常。按永久处理：多半是代码 bug，重试不会自愈。</summary>
    public const string Unclassified = "unclassified";
}

/// <summary>一次 Embedding 失败的归因结果：码 + 可处置性 + 人类可读消息。</summary>
public sealed record EmbeddingFailure(string Code, FailureKind Kind, string Message)
{
    public bool IsTransient => Kind == FailureKind.Transient;

    /// <summary>
    /// 把异常归到码与可处置性。纯函数：不读时钟、不发请求、不看配置。
    /// </summary>
    /// <remarks>
    /// 默认归 <see cref="FailureKind.Permanent"/> 而非 Transient 是有意的：
    /// 未归类异常通常是代码缺陷，重试只会把一次失败拖成 N 次等待并推迟暴露。
    /// 索引写入是一次性任务，快速失败比"尽力而为"更有价值。
    /// </remarks>
    public static EmbeddingFailure Classify(Exception exception)
    {
        switch (exception)
        {
            // 调用方取消不是失败；区分不了时按 NotFailure 处理，避免把用户中断记成系统故障。
            case OperationCanceledException canceled:
                return canceled.InnerException is TimeoutException
                    ? new(EmbeddingFailureCode.Timeout, FailureKind.Transient, canceled.Message)
                    : new(EmbeddingFailureCode.Cancelled, FailureKind.NotFailure, canceled.Message);

            case TimeoutException timeout:
                return new(EmbeddingFailureCode.Timeout, FailureKind.Transient, timeout.Message);

            // Polly 超时策略抛的是自身异常类型，不继承 OperationCanceledException。
            case var _ when exception.GetType().Name == "TimeoutRejectedException":
                return new(EmbeddingFailureCode.Timeout, FailureKind.Transient, exception.Message);

            case HttpRequestException http:
                return ClassifyHttp(http);

            case System.Net.Sockets.SocketException socket:
                return new(EmbeddingFailureCode.Unavailable, FailureKind.Transient, socket.Message);

            case IOException io:
                return new(EmbeddingFailureCode.Unavailable, FailureKind.Transient, io.Message);

            case System.Text.Json.JsonException json:
                return new(EmbeddingFailureCode.BadResponse, FailureKind.Permanent, json.Message);

            case InvalidOperationException invalid:
                return ClassifyInvalidOperation(invalid);

            default:
                return new(EmbeddingFailureCode.Unclassified, FailureKind.Permanent, exception.Message);
        }
    }

    private static EmbeddingFailure ClassifyHttp(HttpRequestException http)
    {
        // StatusCode 为空表示没走到 HTTP 层：连接拒绝、DNS 失败、TLS 握手失败。全部属暂时性。
        if (http.StatusCode is not { } status)
            return new(EmbeddingFailureCode.Unavailable, FailureKind.Transient, http.Message);

        return (int)status switch
        {
            401 or 403 => new(EmbeddingFailureCode.Unauthorized, FailureKind.Permanent, http.Message),
            404 => new(EmbeddingFailureCode.ModelNotFound, FailureKind.Permanent, http.Message),
            429 => new(EmbeddingFailureCode.RateLimited, FailureKind.Transient, http.Message),
            400 => new(EmbeddingFailureCode.BadRequest, FailureKind.Permanent, http.Message),
            >= 500 => new(EmbeddingFailureCode.Unavailable, FailureKind.Transient, http.Message),
            _ => new(EmbeddingFailureCode.BadRequest, FailureKind.Permanent, http.Message)
        };
    }

    private static EmbeddingFailure ClassifyInvalidOperation(InvalidOperationException invalid)
        => invalid.Message switch
        {
            var m when m.Contains("未配置", StringComparison.Ordinal) =>
                new(EmbeddingFailureCode.NotConfigured, FailureKind.Permanent, m),
            var m when m.Contains("不匹配", StringComparison.Ordinal) =>
                new(EmbeddingFailureCode.CountMismatch, FailureKind.Permanent, m),
            var m => new(EmbeddingFailureCode.Unclassified, FailureKind.Permanent, m)
        };
}

/// <summary>
/// 装饰器出口的失败异常：携带归因结果，供边界按 <see cref="EmbeddingFailure.Kind"/> 决策。
/// </summary>
/// <remarks>
/// 之所以仍用异常而不是 Result 类型：调用方（ChunkIndexer / HybridRetriever）已经在 catch 上，
/// 换成 Result 会让三层签名同时变形；而边界需要的"为什么失败"由 <see cref="Failure"/> 完整携带，
/// 不损失任何信息。Permanent 与 Transient 的区别体现在 <see cref="EmbeddingFailure.Kind"/> 上，
/// 不在异常类型上——避免调用方为了区分而写两段 catch。
/// </remarks>
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(EmbeddingFailure failure, Exception inner)
        : base($"Embedding 调用失败（{failure.Code}）：{failure.Message}", inner)
    {
        Failure = failure;
    }

    public EmbeddingFailure Failure { get; }
}

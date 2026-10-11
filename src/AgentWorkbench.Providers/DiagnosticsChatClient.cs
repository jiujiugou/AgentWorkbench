using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Providers;

/// <summary>
/// 诊断中间件：捕获模型端非成功响应，记录**完整** HTTP 状态码与 response body，再原样抛出。
/// 用于把 OpenAI 兼容端点（如 SiliconFlow）返回的 400 具体错误从"只有 Status"变成可定位的正文。
/// </summary>
/// <remarks>
/// 三条设计约定：
/// <list type="number">
/// <item>只记录、不改变语义 —— 异常原样 rethrow，不包装、不吞掉，调用方看到的异常类型不变。</item>
/// <item>位置最靠近真实 HTTP 客户端，位于 ModelRuntime 的重试装饰器<b>之下</b>：
/// 它拿到的是供应商的原始失败，而 ModelRuntime 的重试判定看到的是同一批异常，两层互不干扰。</item>
/// <item>存在的理由：OpenAI 兼容端点的 4xx 原因几乎全在响应体里（「不支持 tools」「模型不存在」等），
/// 标准异常只给一个状态码；没有这层就只能看到一句无信息量的失败。</item>
/// </list>
/// </remarks>
public sealed class DiagnosticsChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;

    /// <summary>
    /// 用 <paramref name="innerClient"/> 建立装饰链。
    /// </summary>
    /// <param name="innerClient">被装饰的真实客户端（由 <see cref="ChatClientFactory"/> 创建的 OpenAI 兼容客户端）。</param>
    /// <param name="logger">
    /// 日志器，由 <see cref="ChatClientFactory"/> 传入 —— 与端点缓存同生命周期，因此日志归属「模型网关」这一上下文。
    /// </param>
    public DiagnosticsChatClient(IChatClient innerClient, ILogger logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            // 非流式请求只有一个失败抛出点，记录后原样抛出即可。
            Log(ex, options);
            throw;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 流式的失败可能发生在「流已开始之后」的任意一次 MoveNextAsync 上（例如首包正常、中途返回错误帧），
        // 因此必须逐次驱动迭代器并就地捕获。这里手写 enumerator 而非 await foreach，
        // 原因是 C# 不允许在带 catch 的 try 块内 yield return —— 只有手动调用 MoveNextAsync
        // 才能同时做到「捕获并记录异常」与「继续 yield 增量」。
        await using var enumerator = base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate current;
            try
            {
                if (!await enumerator.MoveNextAsync())
                    break;
                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                // 记录后照常抛出：消费方（Conversation / AgentHub）据此走各自的错误或降级分支。
                Log(ex, options);
                throw;
            }

            yield return current;
        }
    }

    /// <summary>
    /// 记录失败详情。<see cref="ClientResultException"/> 是 OpenAI SDK 对「服务端返回了非成功状态码」的表示，
    /// 能取到原始 HTTP 响应；其他异常（连接失败、超时、反序列化失败等）按普通异常记录。
    /// </summary>
    /// <param name="ex">捕获到的异常，原样记录（不吞、不替换）。</param>
    /// <param name="options">本次请求的选项，用于日志中带上模型名与工具数量。</param>
    /// <remarks>
    /// 额外记录 Model 与 Tools 数量，是为了让「带工具的请求被 4xx 拒绝」这类问题一眼可辨
    /// —— 它对应 Conversation 侧「带工具失败 → 去掉工具重试一次」的降级路径。
    /// </remarks>
    private void Log(Exception ex, ChatOptions? options)
    {
        if (ex is ClientResultException clientError)
        {
            // 取原始响应：状态码 / 原因短语 / 正文。正文是定位问题的关键，通常直接写明哪个字段不合法。
            var response = clientError.GetRawResponse();
            var body = response?.Content?.ToString();
            _logger.LogError(clientError,
                "模型请求失败（服务端拒绝）：Status={Status} Reason={Reason} Model={Model} Tools={Tools} Body={Body}",
                clientError.Status,
                response?.ReasonPhrase,
                options?.ModelId,
                options?.Tools?.Count ?? 0,
                string.IsNullOrWhiteSpace(body) ? "(empty)" : body);
        }
        else
        {
            // 没有 HTTP 响应可读（如 DNS 失败、连接被拒、超时），只记异常本身。
            _logger.LogError(ex, "模型请求失败：Model={Model} Tools={Tools}", options?.ModelId, options?.Tools?.Count ?? 0);
        }
    }
}

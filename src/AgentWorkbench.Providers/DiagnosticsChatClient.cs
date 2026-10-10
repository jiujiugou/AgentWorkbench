using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Providers;

/// <summary>
/// 诊断中间件：捕获模型端非成功响应，记录**完整** HTTP 状态码与 response body，再原样抛出。
/// 用于把 OpenAI 兼容端点（如 SiliconFlow）返回的 400 具体错误从"只有 Status"变成可定位的正文。
/// </summary>
public sealed class DiagnosticsChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;

    public DiagnosticsChatClient(IChatClient innerClient, ILogger logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            Log(ex, options);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
                Log(ex, options);
                throw;
            }

            yield return current;
        }
    }

    private void Log(Exception ex, ChatOptions? options)
    {
        if (ex is ClientResultException clientError)
        {
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
            _logger.LogError(ex, "模型请求失败：Model={Model} Tools={Tools}", options?.ModelId, options?.Tools?.Count ?? 0);
        }
    }
}

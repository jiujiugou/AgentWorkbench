namespace AgentWorkbench.Tools.Mcp;

/// <summary>
/// 传输故障 vs 对方明确拒绝。判的是一件事：<b>副作用到底有没有发生，我们知不知道</b>。
/// </summary>
/// <remarks>
/// 连不上 / 超时 = 不知道（要查证，不能重放）；4xx / 校验错 / 权限错 = 对方明确没接受（保持 Failed）。
/// <para>
/// 为什么要单独判：MCP 服务器把网络异常和对方拒绝都压成同一种 <c>isError=true</c> 文本，
/// 全拍成 Failed 会让「其实成功了却报失败」的盲区把人诱去重试，造成重复副作用。
/// </para>
/// <para>
/// frappe_mcp 侧对应 <c>FrappeConnectionError</c>（"Cannot reach Frappe" / "timed out"）与 httpx 超时；
/// C# 侧对应 <see cref="System.Net.Http.HttpRequestException"/> / <see cref="TimeoutException"/>。
/// 判据集中在文本关键字与异常类型两处：新增文案只会漏判为 Failed（保守：不查证、不重放），
/// 不会误把拒绝当超时——这是刻意的失败方向。
/// </para>
/// </remarks>
public static class ToolFailureClassifier
{
    /// <param name="ex">C# 异常；工具级 isError 时为 null，只按 <paramref name="message"/> 判。</param>
    /// <param name="message">错误文本（工具 isError 内容或异常消息）。</param>
    public static bool IsTransportFailure(Exception? ex, string message)
    {
        if (ex is System.Net.Http.HttpRequestException or TimeoutException
            or System.IO.IOException)
            return true;

        // 调用方主动取消已在调用处重新抛出，不会到这里；这里剩下的 TaskCanceledException 即超时。
        if (ex is TaskCanceledException)
            return true;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Cannot reach Frappe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("FrappeConnectionError", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ConnectError", StringComparison.OrdinalIgnoreCase)
            || message.Contains("tool_unavailable", StringComparison.OrdinalIgnoreCase);
    }
}

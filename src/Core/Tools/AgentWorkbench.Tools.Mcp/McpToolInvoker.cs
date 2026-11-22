using System.Text.Json;
using AgentWorkbench.Ports;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>
/// 把 MCP 注册表当成一个 <see cref="IToolInvoker"/>：按全名取工具，调用，原样交回文本。
/// </summary>
/// <remarks>
/// <para>
/// 这一层只做三件事：查名字、拼参数、调用。它<b>不</b>判断这次调用该不该发生 ——
/// 那是动作闸的事；也<b>不</b>解释返回体的字段含义 —— 那是动作域的事。
/// 一旦在这里加任何业务判断，动作域就会被底座「理解」，换域时 diff 不再为空。
/// </para>
/// <para>
/// 查名字只查<b>已准入</b>的工具（注册表里的全部），不区分「能不能给模型看」：
/// 进不了模型目录的那些本来就是留给这条路径用的。
/// </para>
/// </remarks>
public sealed class McpToolInvoker(McpToolRegistry registry) : IToolInvoker
{
    public bool Has(string toolName)
        => !string.IsNullOrWhiteSpace(toolName)
           && registry.Tools.Any(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));

    public async ValueTask<ToolInvocationResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken ct)
    {
        var tool = registry.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
            return ToolInvocationResult.Failure(
                ToolInvocationCodes.NotFound, $"工具不在当前目录：{toolName}");

        if (tool is not AIFunction function)
            return ToolInvocationResult.Failure(
                ToolInvocationCodes.Unavailable, $"工具不是可调用的函数：{toolName}");

        AIFunctionArguments callArguments;
        try
        {
            callArguments = BuildArguments(arguments);
        }
        catch (Exception ex)
        {
            return ToolInvocationResult.Failure(
                ToolInvocationCodes.Failed, $"参数无法转换：{ex.GetType().Name}");
        }

        try
        {
            var result = await function.InvokeAsync(callArguments, ct);
            return Normalize(result?.ToString() ?? string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消就是取消本身，不能伪装成「工具失败」——上层要据此区分 Unknown 与中断。
            throw;
        }
        catch (Exception ex)
        {
            // 传输层没通 = 我们不知道对方收到没有，不能当「明确失败」——否则上层不会去查证。
            var code = ToolFailureClassifier.IsTransportFailure(ex, ex.Message)
                ? ToolInvocationCodes.Unavailable
                : ToolInvocationCodes.Failed;
            return ToolInvocationResult.Failure(
                code, $"{ex.GetType().Name}: {Truncate(ex.Message)}");
        }
    }

    /// <summary>
    /// 把 MCP 的调用结果还原成「内容」或「失败」。
    /// </summary>
    /// <remarks>
    /// MCP 的工具级错误<b>不是异常</b>：它照样是一次成功调用，只是返回
    /// <c>{"content":[{"type":"text","text":"..."}],"isError":true}</c>。
    /// 不识别这个形状，一次被对方拒绝的调用会被记成 succeeded —— 而审计里那句话就成了假的。
    /// <para>
    /// 内容侧同理：正常返回把真正的文档 JSON 包在 <c>content[0].text</c> 这个字符串里，
    /// 不拆开的话，动作域声明的业务键路径（<c>data.name</c>）永远取不到。
    /// </para>
    /// </remarks>
    private static ToolInvocationResult Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ToolInvocationResult.Success(string.Empty);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return ToolInvocationResult.Success(raw);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content))
                return ToolInvocationResult.Success(raw);

            var text = JoinTextParts(content);

            var isError = root.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (isError)
            {
                var message = string.IsNullOrWhiteSpace(text) ? "工具返回 isError" : text;
                // frappe_mcp 把网络故障也压成 isError 文本。传输故障 = 不知道对方收到没有，
                // 必须区别于「对方明确拒绝」，上层才会走「只查证不重放」而不是直接判失败。
                var code = ToolFailureClassifier.IsTransportFailure(null, message)
                    ? ToolInvocationCodes.Unavailable
                    : ToolInvocationCodes.Failed;
                return ToolInvocationResult.Failure(code, Truncate(message));
            }

            return ToolInvocationResult.Success(string.IsNullOrWhiteSpace(text) ? raw : text);
        }
    }


    private static string JoinTextParts(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
                parts.Add(text.GetString() ?? string.Empty);
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// JSON 对象 → 调用参数。值保持 <see cref="JsonElement"/> 原样传入，
    /// 由 MEA 按工具自己的 schema 绑定；这里不猜类型、不做字符串化。
    /// </summary>
    private static AIFunctionArguments BuildArguments(JsonElement arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }

        return new AIFunctionArguments(values);
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}

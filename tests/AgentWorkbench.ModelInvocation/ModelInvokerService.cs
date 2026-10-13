using System.Text.Json;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.ModelInvocation;

/// <summary>
/// 单步模型调用的唯一实现：解析端点 → 取客户端 → 统一语义包装 → 一次调用 → 协议 DTO 回传。
/// </summary>
/// <remarks>
/// 复用而非重写：端点解析与客户端缓存走 <see cref="IChatClientFactory"/>（Providers），
/// 超时/重试/并发闸门/用量计量走 <see cref="IModelRuntime.Wrap"/>（ModelRuntime）——
/// 与 src 侧所有模型消费者共用同一套调用语义，本服务不发明第二套。
/// </remarks>
public sealed class ModelInvokerService : IModelInvoker
{
    private readonly IChatClientFactory _chatFactory;
    private readonly IModelRuntime _runtime;
    private readonly ILogger<ModelInvokerService> _logger;

    public ModelInvokerService(
        IChatClientFactory chatFactory,
        IModelRuntime runtime,
        ILogger<ModelInvokerService> logger)
    {
        _chatFactory = chatFactory;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 解析端点：数据库激活供应商优先，配置回退兜底；两者皆无返回 null（可判定的「未配置」状态）。
        var endpoint = await _chatFactory.ResolveAsync(request.Model, cancellationToken)
            ?? throw new InvalidOperationException("模型未配置：数据库无激活供应商，且 Provider 回退配置缺失");

        // 2) 进程内复用客户端，再包统一调用语义（幂等：已包装的原样返回）。
        var client = _runtime.Wrap(_chatFactory.GetClient(endpoint));

        // 3) 用量前后快照，差值即本次近似增量（并发下含他人增量，见 InvokeUsage 注释）。
        var before = _runtime.GetUsage();

        // 4) 一次调用：tools 以纯声明传入（无执行体），模型要么回文本要么回 tool_calls。
        var options = new ChatOptions();
        var tools = ToAITools(request.Tools);
        if (tools.Count > 0)
            options.Tools = tools;

        var response = await client.GetResponseAsync(ToChatMessages(request.Messages), options, cancellationToken);

        var after = _runtime.GetUsage();

        // 5) 取最后一条 assistant 消息——τ² 每回合只要一条，多模型行为（多条/空）在此收敛。
        var assistant = LastAssistant(response)
            ?? throw new InvalidOperationException("模型未返回 assistant 消息（响应消息列表为空或全非 assistant）");

        var message = ToInvokeMessage(assistant);
        var usage = new InvokeUsage(
            after.Requests - before.Requests,
            after.InputTokens - before.InputTokens,
            after.OutputTokens - before.OutputTokens);

        _logger.LogDebug(
            "Invoke done: model={Model} toolCalls={ToolCallCount} usage(delta)=+{Requests}r +{InputTokens}in +{OutputTokens}out",
            endpoint.Model, message.ToolCalls?.Count ?? 0, usage.Requests, usage.InputTokens, usage.OutputTokens);

        return new InvokeResponse(message, response.ModelId ?? endpoint.Model, usage);
    }

    // ---------- 入向：协议 DTO → M.E.AI 内部类型 ----------

    private static List<ChatMessage> ToChatMessages(IReadOnlyList<InvokeMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            var role = m.Role.ToLowerInvariant() switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                "system" => ChatRole.System,
                _ => throw new ArgumentException($"未知消息角色 '{m.Role}'（允许 user/assistant/tool/system）")
            };

            if (role == ChatRole.Tool)
            {
                // tool 结果：callId 必须带上，否则供应商侧无法关联到上一次调用。
                if (string.IsNullOrWhiteSpace(m.ToolCallId))
                    throw new ArgumentException("tool 消息必须带 toolCallId");
                result.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent(m.ToolCallId!, m.Text ?? string.Empty)
                }));
            }
            else if (role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
            {
                // assistant 工具调用消息：原文 JSON 保留在 RawRepresentation，解析值在 Arguments。
                var contents = new List<AIContent>();
                if (!string.IsNullOrEmpty(m.Text))
                    contents.Add(new TextContent(m.Text));
                foreach (var call in m.ToolCalls)
                    contents.Add(FunctionCallContent.CreateFromParsedArguments(
                        call.Arguments ?? "{}", call.Id, call.Name, ParseArgumentsOrEmpty));
                result.Add(new ChatMessage(ChatRole.Assistant, contents));
            }
            else
            {
                result.Add(new ChatMessage(role, m.Text ?? string.Empty));
            }
        }
        return result;
    }

    private static List<AITool> ToAITools(IReadOnlyList<InvokeTool>? tools)
    {
        if (tools is null || tools.Count == 0)
            return [];

        var result = new List<AITool>(tools.Count);
        foreach (var t in tools)
        {
            // 纯声明：AIFunctionFactory.CreateDeclaration 不绑定任何委托——
            // 工具执行权在 τ² 的 Python orchestrator，.NET 侧只负责「告诉模型有哪些工具」。
            var schema = t.Parameters ?? EmptyObjectSchema();
            result.Add(AIFunctionFactory.CreateDeclaration(t.Name, t.Description ?? string.Empty, schema, null));
        }
        return result;
    }

    // ---------- 出向：M.E.AI 内部类型 → 协议 DTO ----------

    private static ChatMessage? LastAssistant(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
            if (response.Messages[i].Role == ChatRole.Assistant)
                return response.Messages[i];
        return null;
    }

    private static InvokeMessage ToInvokeMessage(ChatMessage assistant)
    {
        List<InvokeToolCall>? calls = null;
        foreach (var content in assistant.Contents)
        {
            if (content is not FunctionCallContent call)
                continue;
            calls ??= [];
            calls.Add(new InvokeToolCall(
                call.CallId ?? string.Empty,
                call.Name,
                SerializeArguments(call.Arguments)));
        }

        return new InvokeMessage(
            "assistant",
            assistant.Text,
            calls is { Count: > 0 } ? calls : null,
            ToolCallId: null);
    }

    // ---------- 小工具 ----------

    private static readonly JsonSerializerOptions ArgJson = new()
    {
        // 参数 JSON 原样字符串进、字符串出，不需要格式化选项；大小写不敏感仅防御性保留。
        PropertyNameCaseInsensitive = true
    };

    /// <summary>工具参数字典 → JSON 字符串。空字典输出 "{}"（OpenAI 协议对无参工具的要求）。</summary>
    private static string SerializeArguments(IDictionary<string, object?>? arguments)
        => arguments is null || arguments.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(arguments, ArgJson);

    /// <summary>参数 JSON → 字典；解析失败退化为空字典（原文已在 CreateFromParsedArguments 保留，不丢信息）。</summary>
    private static IDictionary<string, object?> ParseArgumentsOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>();
        try
        {
            return JsonSerializer.Deserialize<IDictionary<string, object?>>(json, ArgJson)
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            // 标量/畸形 JSON：模型偶尔会吐出非 object 参数。空字典 + 原文保留比整条调用失败好。
            return new Dictionary<string, object?>();
        }
    }

    private static JsonElement EmptyObjectSchema()
    {
        // Clone 后的 JsonElement 独立于 JsonDocument，可安全传出；此处不缓存，
        // 工具声明不频繁，每次 parse 的成本可以忽略。
        using var doc = JsonDocument.Parse("{\"type\":\"object\"}");
        return doc.RootElement.Clone();
    }
}

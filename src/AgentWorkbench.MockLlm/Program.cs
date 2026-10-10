using System.Text.Json;
using System.Text.Json.Serialization;

// AgentWorkbench MockLlm: a deterministic, OpenAI-compatible chat-completions server used as a
// test double for the agent runtime and the workflow engine. It must never call a real model.
//
// Endpoints
//   POST /v1/chat/completions   OpenAI-compatible completion (scenario-driven)
//   GET  /v1/models             model listing (for provider probing)
//   POST /control/scenario      set scenario (?name=normal|harness-tool|harness-loop|reject-tools)
//   POST /control/reset         reset the call counter
//   GET  /control/stats         { scenario, calls }
//
// Scenarios
//   normal        -> plain text answer
//   harness-tool  -> one `calculator` tool call, then a final text answer
//   harness-loop  -> repeated `get_current_time` tool call (must terminate at the loop cap)
//   reject-tools  -> HTTP 400 `unsupported_parameter` when the request carries `tools`

var builder = WebApplication.CreateBuilder(args);

var state = new MockState();

var app = builder.Build();

app.MapGet("/health", () => Results.Json(new { status = "ok", role = "mock-llm" }));

app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data = new[] { new { id = "mock", @object = "model", owned_by = "agentworkbench" } }
}));

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    ChatCompletionRequest? req;
    try
    {
        req = JsonSerializer.Deserialize<ChatCompletionRequest>(body, JsonOptions.Value);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "invalid request json" });
    }

    state.RecordCall();

    // Simulate OpenAI-compatible endpoints (e.g. SiliconFlow GLM-Z1) that reject the `tools` parameter.
    if (state.Scenario == "reject-tools" && req?.Tools is { ValueKind: JsonValueKind.Array } toolsEl && toolsEl.GetArrayLength() > 0)
    {
        return Results.Json(new
        {
            error = new
            {
                message = "Model does not support the 'tools' parameter.",
                type = "invalid_request_error",
                code = "unsupported_parameter",
                param = "tools"
            }
        }, statusCode: 400);
    }

    // Resolve the response: a tool call, or text.
    string? content = null;
    (string Name, string Arguments)? toolCall = null;

    var hasToolResult = req?.Messages?.Any(m => m.Role == "tool") ?? false;
    if (state.Scenario == "harness-loop")
        toolCall = ("get_current_time", "{}");
    else if (state.Scenario == "harness-tool" && !hasToolResult)
        toolCall = ("calculator", "{\"expression\":\"(1+2)*3\"}");
    else
        content = "这是 Mock 助手的最终回答。";

    if (req?.Stream == true)
        return await StreamCompletionAsync(ctx, req, content, toolCall);

    return Results.Json(toolCall is { } tc ? ToolCallCompletion(req, tc.Name, tc.Arguments) : TextCompletion(req, content ?? ""));
});

app.MapPost("/control/scenario", (HttpContext ctx) =>
{
    var name = ctx.Request.Query["name"].ToString();
    if (string.IsNullOrWhiteSpace(name))
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = reader.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("name", out var prop))
                name = prop.GetString() ?? "";
        }
    }

    if (!MockState.IsKnownScenario(name))
        return Results.BadRequest(new { error = $"unknown scenario '{name}'", known = MockState.Names });

    state.Scenario = name;
    return Results.Json(new { scenario = state.Scenario });
});

app.MapPost("/control/reset", () =>
{
    state.Reset();
    return Results.Json(new { calls = state.Calls, scenario = state.Scenario });
});

app.MapGet("/control/stats", () => Results.Json(new
{
    scenario = state.Scenario,
    calls = state.Calls
}));

app.Run();

static object ToolCallCompletion(ChatCompletionRequest? req, string name, string arguments) => new
{
    id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12],
    @object = "chat.completion",
    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    model = req?.Model ?? "mock",
    choices = new[]
    {
        new
        {
            index = 0,
            message = new
            {
                role = "assistant",
                content = (string?)null,
                tool_calls = new[]
                {
                    new { id = "call_mock_1", type = "function", function = new { name, arguments } }
                }
            },
            finish_reason = "tool_calls"
        }
    },
    usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
};

static object TextCompletion(ChatCompletionRequest? req, string content) => new
{
    id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12],
    @object = "chat.completion",
    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    model = req?.Model ?? "mock",
    choices = new[]
    {
        new
        {
            index = 0,
            message = new { role = "assistant", content },
            finish_reason = "stop"
        }
    },
    usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
};

/// <summary>OpenAI-compatible SSE streaming (text or a single tool call).</summary>
static async Task<IResult> StreamCompletionAsync(
    HttpContext ctx, ChatCompletionRequest? req, string? content, (string Name, string Arguments)? toolCall)
{
    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    var id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12];
    var model = req?.Model ?? "mock";
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (toolCall is { } tc)
    {
        var delta = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = "call_mock_1",
                                type = "function",
                                function = new { name = tc.Name, arguments = tc.Arguments }
                            }
                        }
                    },
                    finish_reason = (string?)null
                }
            }
        };
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(delta)}\n\n");

        var finish = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
        };
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(finish)}\n\n");
    }
    else
    {
        var delta = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content },
                    finish_reason = (string?)null
                }
            }
        };
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(delta)}\n\n");

        var finish = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
        };
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(finish)}\n\n");
    }

    await ctx.Response.WriteAsync("data: [DONE]\n\n");
    return Results.Empty;
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Value = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class MockState
{
    public static readonly string[] Names = ["normal", "harness-tool", "harness-loop", "reject-tools"];

    private readonly object _gate = new();
    private int _calls;

    public string Scenario { get; set; } = "normal";
    public int Calls { get { lock (_gate) { return _calls; } } }

    public static bool IsKnownScenario(string? name) => name is not null && Names.Contains(name);

    public void RecordCall()
    {
        lock (_gate)
        {
            _calls++;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _calls = 0;
        }
    }
}

internal sealed record ChatCompletionRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] List<ChatCompletionMessage>? Messages,
    [property: JsonPropertyName("tools")] JsonElement? Tools,
    [property: JsonPropertyName("stream")] bool? Stream);

internal sealed record ChatCompletionMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId);

using System.Text.Json;
using System.Text.Json.Serialization;

// AgentWorkbench MockLlm: a deterministic, OpenAI-compatible chat-completions server used as a
// test double for the agent runtime, the workflow engine, and the τ²-bench adapter.
// It must never call a real model.
//
// Endpoints
//   POST /v1/chat/completions   OpenAI-compatible completion (scenario-driven)
//   GET  /v1/models             model listing (for provider probing)
//   GET  /health                health check
//   POST /control/scenario      set scenario (?name=normal|harness-tool|harness-loop|reject-tools|replay)
//   POST /control/fixture       load a replay fixture and switch to replay
//                               (body: {"turns":[...]} or raw JSONL, one turn object per line)
//   POST /control/reset         reset the call counter and the replay cursor (fixture is kept)
//   GET  /control/state         { scenario, calls, turn, fixtureTurns }
//   GET  /control/stats         { scenario, calls }
//   GET  /control/last-request  compact summary of the most recent request (DTO round-trip check)
//
// Scenarios
//   normal        -> plain text answer
//   harness-tool  -> one `calculator` tool call, then a final text answer
//   harness-loop  -> repeated `get_current_time` tool call (must terminate at the loop cap)
//   reject-tools  -> HTTP 400 `unsupported_parameter` when the request carries `tools`
//   replay        -> serve fixture[turn] verbatim (text and/or any number of tool_calls);
//                    exhausted fixture -> HTTP 500 `fixture_exhausted` (loud, never silent wraparound)

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
    ChatCompletionRequest? req;
    try
    {
        req = await ctx.Request.ReadFromJsonAsync<ChatCompletionRequest>(JsonOptions.Value, ctx.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "invalid request json" });
    }

    state.RecordCall(req);

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

    // Resolve the response: replay fixture entry, scripted tool call(s), or plain text.
    string? content = null;
    List<(string Id, string Name, string Arguments)> toolCalls = [];

    if (state.Scenario == "replay")
    {
        var entry = state.NextReplayTurn();
        if (entry is null)
        {
            return Results.Json(new
            {
                error = new
                {
                    message = $"Replay fixture exhausted at turn {state.Turn} (fixture has {state.FixtureTurns} turns). " +
                              "The agent asked for more model calls than the fixture scripts.",
                    type = "fixture_exhausted",
                    code = "fixture_exhausted"
                }
            }, statusCode: 500);
        }

        content = entry.Content;
        for (var i = 0; i < (entry.ToolCalls?.Count ?? 0); i++)
        {
            var call = entry.ToolCalls![i];
            toolCalls.Add((call.Id ?? $"call_mock_{state.Turn}_{i}", call.Name, call.Arguments ?? "{}"));
        }
        state.CommitReplayTurn();
    }
    else
    {
        var hasToolResult = req?.Messages?.Any(m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)) ?? false;
        if (state.Scenario == "harness-loop")
            toolCalls.Add(("call_mock_1", "get_current_time", "{}"));
        else if (state.Scenario == "harness-tool" && !hasToolResult)
            toolCalls.Add(("call_mock_1", "calculator", "{\"expression\":\"(1+2)*3\"}"));
        else
            content = "这是 Mock 助手的最终回答。";
    }

    if (req?.Stream == true)
        return await StreamCompletionAsync(ctx, req, content, toolCalls);

    return Results.Json(
        toolCalls.Count > 0 ? ToolCallCompletion(req, content, toolCalls) : TextCompletion(req, content ?? ""));
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

    // replay without a fixture is a broken state: refuse the switch instead of failing later at call time.
    if (name == "replay" && state.FixtureTurns == 0)
        return Results.BadRequest(new { error = "scenario 'replay' requires a fixture: POST /control/fixture first" });

    state.SetScenario(name);
    return Results.Json(new { scenario = state.Scenario });
});

app.MapPost("/control/fixture", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var raw = await reader.ReadToEndAsync();

    List<ReplayTurn> turns;
    try
    {
        turns = ReplayFixture.Parse(raw);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"fixture is not valid JSON: {ex.Message}" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    state.LoadFixture(turns);
    return Results.Json(new { scenario = state.Scenario, fixtureTurns = state.FixtureTurns });
});

app.MapPost("/control/reset", () =>
{
    state.Reset();
    return Results.Json(new { calls = state.Calls, scenario = state.Scenario, turn = state.Turn });
});

app.MapGet("/control/state", () => Results.Json(new
{
    scenario = state.Scenario,
    calls = state.Calls,
    turn = state.Turn,
    fixtureTurns = state.FixtureTurns
}));

app.MapGet("/control/stats", () => Results.Json(new
{
    scenario = state.Scenario,
    calls = state.Calls
}));

// DTO round-trip surface: lets a probe assert that a request carrying `tool_calls` history,
// `role=tool` results and `tool_call_id` was parsed without loss.
app.MapGet("/control/last-request", () => Results.Json(new
{
    seen = state.LastRequestSeen,
    model = state.LastRequest?.Model,
    toolCount = state.LastRequest?.Tools is { ValueKind: JsonValueKind.Array } t ? t.GetArrayLength() : 0,
    messages = state.LastRequest?.Messages?.Select(m => new
    {
        role = m.Role,
        text = m.Content,
        toolCalls = m.ToolCalls is { ValueKind: JsonValueKind.Array } a ? a.GetArrayLength() : 0,
        toolCallId = m.ToolCallId
    }).ToArray()
}));

app.Run();

static object ToolCallCompletion(ChatCompletionRequest? req, string? content, List<(string Id, string Name, string Arguments)> calls) => new
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
                content,
                tool_calls = calls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.Arguments }
                }).ToArray()
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

/// <summary>OpenAI-compatible SSE streaming (text and/or any number of tool calls).</summary>
static async Task<IResult> StreamCompletionAsync(
    HttpContext ctx, ChatCompletionRequest? req, string? content, List<(string Id, string Name, string Arguments)> calls)
{
    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    var id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12];
    var model = req?.Model ?? "mock";
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (calls.Count > 0)
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
                        tool_calls = calls.Select((c, i) => new
                        {
                            index = i,
                            id = c.Id,
                            type = "function",
                            function = new { name = c.Name, arguments = c.Arguments }
                        }).ToArray()
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

/// <summary>One scripted model turn: text and/or tool calls. Both may be present (the adapter normalizes).</summary>
internal sealed record ReplayTurn(string? Content, List<ReplayToolCall>? ToolCalls);

/// <summary><paramref name="Arguments"/> is always a JSON string ("{}" when absent).</summary>
internal sealed record ReplayToolCall(string? Id, string Name, string? Arguments);

/// <summary>
/// Fixture parser. Accepts either <c>{"turns":[{"content":...},{"tool_calls":[...]}]}</c>
/// or raw JSONL (one turn object per line; blank lines and <c>#</c> comments ignored).
/// </summary>
internal static class ReplayFixture
{
    public static List<ReplayTurn> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("fixture is empty");

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("fixture must be a JSON object or JSONL");

            if (root.TryGetProperty("turns", out var turnsEl))
            {
                if (turnsEl.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("fixture 'turns' must be an array");
                var list = new List<ReplayTurn>();
                foreach (var item in turnsEl.EnumerateArray())
                    list.Add(ParseTurn(item));
                if (list.Count == 0)
                    throw new ArgumentException("fixture 'turns' is empty");
                return list;
            }

            // A bare single-turn object is accepted for convenience.
            return [ParseTurn(root)];
        }

        var jsonl = new List<ReplayTurn>();
        var lineNo = 0;
        foreach (var line in raw.Split('\n'))
        {
            lineNo++;
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                jsonl.Add(ParseTurn(doc.RootElement));
            }
            catch (JsonException ex)
            {
                throw new JsonException($"JSONL line {lineNo} is not valid JSON: {ex.Message}");
            }
        }

        if (jsonl.Count == 0)
            throw new ArgumentException("fixture has no turns");
        return jsonl;
    }

    private static ReplayTurn ParseTurn(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("each fixture turn must be a JSON object");

        string? content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        List<ReplayToolCall>? calls = null;
        if (el.TryGetProperty("tool_calls", out var tc))
        {
            if (tc.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                throw new ArgumentException("fixture 'tool_calls' must be an array");
            if (tc.ValueKind == JsonValueKind.Array)
            {
                calls = [];
                foreach (var item in tc.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException("fixture tool_call is missing 'name'");

                    string? id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                        ? i.GetString()
                        : null;

                    var args = "{}";
                    if (item.TryGetProperty("arguments", out var a))
                        args = a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "{}") : a.GetRawText();

                    calls.Add(new ReplayToolCall(id, name!, args));
                }
            }
        }

        if (string.IsNullOrEmpty(content) && calls is null or { Count: 0 })
            throw new ArgumentException("each fixture turn must carry 'content' or 'tool_calls'");

        return new ReplayTurn(content, calls);
    }
}

internal sealed class MockState
{
    public static readonly string[] Names = ["normal", "harness-tool", "harness-loop", "reject-tools", "replay"];

    private readonly object _gate = new();
    private int _calls;
    private int _turn;
    private List<ReplayTurn> _fixture = [];
    private ChatCompletionRequest? _lastRequest;

    public string Scenario { get; private set; } = "normal";
    public int Calls { get { lock (_gate) { return _calls; } } }
    public int Turn { get { lock (_gate) { return _turn; } } }
    public int FixtureTurns { get { lock (_gate) { return _fixture.Count; } } }
    public bool LastRequestSeen { get { lock (_gate) { return _lastRequest is not null; } } }
    public ChatCompletionRequest? LastRequest { get { lock (_gate) { return _lastRequest; } } }

    public static bool IsKnownScenario(string? name) => name is not null && Names.Contains(name);

    public void RecordCall(ChatCompletionRequest? request)
    {
        lock (_gate)
        {
            _calls++;
            _lastRequest = request;
        }
    }

    public void SetScenario(string name)
    {
        lock (_gate)
        {
            Scenario = name;
        }
    }

    public void LoadFixture(List<ReplayTurn> turns)
    {
        lock (_gate)
        {
            _fixture = turns;
            _turn = 0;
            _calls = 0;
            Scenario = "replay";
        }
    }

    public ReplayTurn? NextReplayTurn()
    {
        lock (_gate)
        {
            return _turn < _fixture.Count ? _fixture[_turn] : null;
        }
    }

    public void CommitReplayTurn()
    {
        lock (_gate)
        {
            _turn++;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _calls = 0;
            _turn = 0;
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
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId,
    [property: JsonPropertyName("tool_calls")] JsonElement? ToolCalls);

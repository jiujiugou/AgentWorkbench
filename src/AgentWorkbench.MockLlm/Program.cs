using System.Text.Json;
using System.Text.Json.Serialization;

// AgentWorkbench MockLlm: a deterministic, OpenAI-compatible chat-completions server used by
// scripts/acceptance-p3.ps1 and scripts/eval-p3.ps1. It must never call a real model.
//
// Endpoints
//   POST /v1/chat/completions   OpenAI-compatible completion (scenario-driven)
//   GET  /v1/models             model listing (for provider probing)
//   POST /control/scenario      set scenario (?name=normal|badref|loop|illegal|nonjson)
//   POST /control/reset         reset the call counter
//   GET  /control/stats         { scenario, calls, callsByContext }
//
// Scenario fixtures are loaded from --fixtures <dir> (default: tests/fixtures/llm).

var builder = WebApplication.CreateBuilder(args);

var fixturesDir = ResolveFixturesDir(args);
var scenarios = ScenarioLibrary.Load(fixturesDir);

var state = new MockState();

var app = builder.Build();

app.MapGet("/health", () => Results.Json(new { status = "ok", role = "mock-llm", fixtures = fixturesDir }));

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

    var system = string.Join("\n", req?.Messages?.Where(m => m.Role == "system").Select(m => m.Content) ?? []);
    var user = req?.Messages?.LastOrDefault(m => m.Role == "user")?.Content ?? "";
    var context = Classify(system, user);

    var scenario = context == "extract" ? "extract" : state.Scenario;
    state.RecordCall(context);

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

    // Optional artificial delay (extraction context only) so callers can deterministically observe a run
    // while it is still executing (used by acceptance N4: "未就绪 run -> 400").
    if (context == "extract" && state.DelayMs > 0)
        await Task.Delay(state.DelayMs);

    // Resolve the response: a tool call, or text.
    string? content = null;
    (string Name, string Arguments)? toolCall = null;

    if (context == "other")
    {
        var hasToolResult = req?.Messages?.Any(m => m.Role == "tool") ?? false;
        if (state.Scenario == "harness-loop")
            toolCall = ("get_current_time", "{}");
        else if (state.Scenario == "harness-tool" && !hasToolResult)
            toolCall = ("calculator", "{\"expression\":\"(1+2)*3\"}");
        else
            content = "这是 Mock 助手的最终回答。";
    }
    else
    {
        // Agent 审查上下文：用原生 tool_calls 驱动 FunctionInvokingChatClient。
        // loop    = 持续调用白名单工具（locate_evidence），必须到 maxSteps 终止。
        // illegal = 请求白名单外工具（drop_table），必须被拒绝并留痕。
        if (context == "agent" && state.Scenario == "loop")
            toolCall = ("locate_evidence", "{\"keyword\":\"违约\"}");
        else if (context == "agent" && state.Scenario == "illegal")
            toolCall = ("drop_table", "{\"keyword\":\"x\"}");
        else
            content = scenarios.Render(scenario, user);
    }

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

    if (!ScenarioLibrary.IsKnown(name))
        return Results.BadRequest(new { error = $"unknown scenario '{name}'", known = ScenarioLibrary.Names });

    state.Scenario = name;
    return Results.Json(new { scenario = state.Scenario });
});

app.MapPost("/control/reset", () =>
{
    state.Reset();
    return Results.Json(new { calls = state.Calls, scenario = state.Scenario });
});

app.MapPost("/control/delay", (HttpContext ctx) =>
{
    var raw = ctx.Request.Query["ms"].ToString();
    if (!int.TryParse(raw, out var ms) || ms < 0)
        return Results.BadRequest(new { error = "ms must be a non-negative integer" });

    state.DelayMs = ms;
    return Results.Json(new { delayMs = state.DelayMs });
});

app.MapGet("/control/stats", () => Results.Json(new
{
    scenario = state.Scenario,
    calls = state.Calls,
    delayMs = state.DelayMs,
    callsByContext = state.CallsByContext
}));

app.Run();

static string Classify(string system, string user)
{
    var text = system + "\n" + user;
    if (text.Contains("抽取要素"))
        return "extract";
    if (text.Contains("待审查问题"))
        return "agent";
    return "other";
}

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

static string ResolveFixturesDir(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--fixtures" or "-f")
            return Path.GetFullPath(args[i + 1]);
    }

    var candidate = Path.Combine(Directory.GetCurrentDirectory(), "tests", "fixtures", "llm");
    return Path.GetFullPath(candidate);
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
    private readonly object _gate = new();
    private int _calls;
    private readonly Dictionary<string, int> _byContext = new(StringComparer.Ordinal);

    public string Scenario { get; set; } = "normal";
    public int DelayMs { get; set; }
    public int Calls { get { lock (_gate) { return _calls; } } }
    public IReadOnlyDictionary<string, int> CallsByContext { get { lock (_gate) { return new Dictionary<string, int>(_byContext); } } }

    public void RecordCall(string context)
    {
        lock (_gate)
        {
            _calls++;
            _byContext[context] = _byContext.TryGetValue(context, out var n) ? n + 1 : 1;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _calls = 0;
            _byContext.Clear();
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

/// <summary>Scenario fixtures loaded from tests/fixtures/llm/*.json with in-code fallbacks.</summary>
internal sealed class ScenarioLibrary
{
    public static readonly string[] Names = ["normal", "badref", "loop", "illegal", "nonjson", "extract", "harness-tool", "harness-loop", "reject-tools"];

    private readonly Dictionary<string, ScenarioFixture> _fixtures = new(StringComparer.Ordinal);

    private ScenarioLibrary() { }

    public static bool IsKnown(string? name) => name is not null && Names.Contains(name);

    public static ScenarioLibrary Load(string dir)
    {
        var library = new ScenarioLibrary();
        foreach (var name in Names)
            library._fixtures[name] = ScenarioFixture.Default(name);

        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var fixture = JsonSerializer.Deserialize<ScenarioFixture>(File.ReadAllText(file), JsonOptions.Value);
                    if (fixture is not null && !string.IsNullOrWhiteSpace(fixture.Scenario))
                        library._fixtures[fixture.Scenario!] = fixture;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"MockLlm: failed to load fixture {file}: {ex.Message}");
                }
            }
        }

        return library;
    }

    public string Render(string scenario, string userPrompt)
    {
        if (!_fixtures.TryGetValue(scenario, out var fixture) && _fixtures.TryGetValue("normal", out var fallback))
            fixture = fallback;
        fixture ??= ScenarioFixture.Default("normal");

        return fixture.Kind switch
        {
            "raw" => fixture.Content ?? string.Empty,
            "agent-tool" => BuildTool(fixture),
            "agent-final" => BuildFinal(fixture, citation: fixture.Citations?.FirstOrDefault()),
            "agent-final-dynamic" => BuildDynamicFinal(userPrompt),
            _ => "{}"
        };
    }

    private static string BuildTool(ScenarioFixture fixture)
        => JsonSerializer.Serialize(new
        {
            action = "tool",
            tool = fixture.Tool ?? "locate_evidence",
            keyword = fixture.Keyword ?? "违约"
        });

    private static string BuildFinal(ScenarioFixture fixture, ScenarioCitation? citation)
    {
        var citations = citation is null
            ? Array.Empty<object>()
            : [new { seq = citation.Seq, evidence = citation.Evidence }];

        return JsonSerializer.Serialize(new
        {
            action = "final",
            title = fixture.Title ?? "审查结论",
            conclusion = fixture.Conclusion ?? "依据证据形成结论。",
            suggestion = fixture.Suggestion ?? "建议人工复核并补正。",
            impact = fixture.Impact ?? "条款层面存在不一致。",
            citations
        });
    }

    private static string BuildDynamicFinal(string userPrompt)
    {
        var (seq, evidence) = FirstObservation(userPrompt);
        var citations = seq is null
            ? Array.Empty<object>()
            : [new { seq = seq.Value, evidence = evidence! }];

        return JsonSerializer.Serialize(new
        {
            action = "final",
            title = "依据原文证据的审查结论",
            conclusion = evidence is null ? "资料不足。" : $"已定位到可核对原文：{Truncate(evidence, 40)}",
            suggestion = "建议核对上述原文后完成文档层面补正。",
            impact = "该问题影响条款层面的表述一致性。",
            citations
        });
    }

    private static (int? seq, string? evidence) FirstObservation(string userPrompt)
    {
        foreach (var raw in userPrompt.Split('\n'))
        {
            var line = raw.Trim();
            var marker = line.IndexOf("seq=", StringComparison.Ordinal);
            if (marker < 0)
                continue;
            var separator = line.IndexOf("::", marker, StringComparison.Ordinal);
            if (separator < 0)
                continue;

            var seqText = line[(marker + 4)..separator].Trim();
            var evidence = line[(separator + 2)..].Trim();
            if (int.TryParse(seqText, out var seq) && evidence.Length > 0)
                return (seq, evidence);
        }
        return (null, null);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

internal sealed class ScenarioFixture
{
    [JsonPropertyName("scenario")] public string? Scenario { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "agent-final-dynamic";
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("keyword")] public string? Keyword { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
    [JsonPropertyName("suggestion")] public string? Suggestion { get; set; }
    [JsonPropertyName("impact")] public string? Impact { get; set; }
    [JsonPropertyName("citations")] public List<ScenarioCitation>? Citations { get; set; }

    public static ScenarioFixture Default(string scenario) => scenario switch
    {
        "normal" => new ScenarioFixture { Scenario = "normal", Kind = "agent-final-dynamic" },
        "badref" => new ScenarioFixture
        {
            Scenario = "badref", Kind = "agent-final",
            Title = "引用校验负控", Conclusion = "尝试给出不可核对的引用。",
            Suggestion = "人工复核。", Impact = "无。",
            Citations = [new ScenarioCitation { Seq = 999999, Evidence = "__NOT_IN_DOCUMENT__" }]
        },
        "loop" => new ScenarioFixture
        {
            Scenario = "loop", Kind = "agent-tool", Tool = "locate_evidence", Keyword = "违约"
        },
        "illegal" => new ScenarioFixture
        {
            Scenario = "illegal", Kind = "agent-tool", Tool = "drop_table", Keyword = "x"
        },
        "nonjson" => new ScenarioFixture
        {
            Scenario = "nonjson", Kind = "raw", Content = "这不是合法的 JSON 响应"
        },
        "extract" => new ScenarioFixture
        {
            Scenario = "extract", Kind = "raw", Content = "{}"
        },
        "harness-tool" => new ScenarioFixture
        {
            Scenario = "harness-tool", Kind = "raw", Content = "{}"
        },
        "harness-loop" => new ScenarioFixture
        {
            Scenario = "harness-loop", Kind = "raw", Content = "{}"
        },
        "reject-tools" => new ScenarioFixture
        {
            Scenario = "reject-tools", Kind = "raw", Content = "{}"
        },
        _ => new ScenarioFixture { Scenario = scenario, Kind = "agent-final-dynamic" }
    };
}

internal sealed class ScenarioCitation
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }
}

using System.Text.Json;
using AgentWorkbench.Agent;
using AgentWorkbench.ReviewAdvisor.Evidence;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.ReviewAdvisor.Agent;

/// <summary>
/// 受限 Agent Loop（基于 Microsoft.Extensions.AI 的 <see cref="FunctionInvokingChatClient"/> 原生函数调用）。
/// 模型在只读工具白名单内迭代取证，直到给出带引用的结论或触达预算。
/// 白名单通过"只注册三个只读工具"强制；越权工具调用触发终止并留痕（<see cref="FunctionInvokingChatClient.TerminateOnUnknownCalls"/>）。
/// 业务约束（无证据短路、引用后置校验）仍由程序保证。
/// </summary>
public sealed class ReviewAgentLoop
{
    public const int DefaultMaxSteps = 6;

    private static readonly HashSet<string> AllowedTools =
        new(StringComparer.Ordinal) { "retrieve_evidence", "read_chunk", "locate_evidence" };

    private const int MaxTraceChars = 400;

    private readonly IChatClientProvider _clientProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<ReviewAgentLoop> _logger;

    public ReviewAgentLoop(
        IChatClientProvider clientProvider,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        ILogger<ReviewAgentLoop> logger)
    {
        _clientProvider = clientProvider;
        _loggerFactory = loggerFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<AdvisorLoopResult> RunAsync(
        ReviewFindingRecord finding,
        AdvisorToolContext context,
        IReadOnlyList<string> seedObservations,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        var steps = new List<AdvisorStepDraft>();

        var client = await _clientProvider.GetClientAsync(cancellationToken);
        if (client is null)
        {
            steps.Add(new AdvisorStepDraft(finding.Id, 0, "assistant", null, null, "模型不可用"));
            return Insufficient(finding, steps, 0, "模型不可用");
        }

        // 不要 using：底层 IChatClient 由模型网关缓存共享，Dispose 会破坏后续调用。
        var invoking = new FunctionInvokingChatClient(client, _loggerFactory, _services)
        {
            MaximumIterationsPerRequest = Math.Max(1, maxSteps),
            TerminateOnUnknownCalls = true,
            AllowConcurrentInvocation = false,
        };

        invoking.FunctionInvoker = async (invocation, token) =>
        {
            var arguments = SerializeArguments(invocation.Arguments);
            var result = await invocation.Function.InvokeAsync(invocation.Arguments, token);
            steps.Add(new AdvisorStepDraft(
                finding.Id, steps.Count, "tool", invocation.Function.Name, arguments, Truncate(result?.ToString())));
            return result;
        };

        var options = new ChatOptions
        {
            Tools = BuildTools(context),
            Temperature = 0.2f,
            AllowMultipleToolCalls = false,
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AdvisorPrompts.System),
            new(ChatRole.User, AdvisorPrompts.BuildUser(finding, seedObservations)),
        };

        ChatResponse response;
        try
        {
            response = await invoking.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent Loop 运行失败 finding={FindingId}", finding.Id);
            steps.Add(new AdvisorStepDraft(finding.Id, steps.Count, "assistant", null, null, "模型输出不可用"));
            return Insufficient(finding, steps, ToolStepCount(steps), "模型输出不可用");
        }

        // 预算耗尽或越权工具会让循环终止并带回未完成的函数调用。
        var unresolved = LastFunctionCalls(response);
        if (unresolved.Count > 0)
        {
            var unknown = unresolved.FirstOrDefault(c => !AllowedTools.Contains(c.Name));
            if (unknown is not null)
            {
                steps.Add(new AdvisorStepDraft(
                    finding.Id, steps.Count, "tool", unknown.Name, SerializeCall(unknown), "拒绝：工具不在白名单"));
                return Insufficient(finding, steps, ToolStepCount(steps), "越权工具被拒绝");
            }

            steps.Add(new AdvisorStepDraft(finding.Id, steps.Count, "assistant", null, null, "达到最大步数，未收敛"));
            return Insufficient(finding, steps, ToolStepCount(steps), "达到最大步数，未收敛");
        }

        var answer = TryParse(response.Text);
        if (answer is null)
        {
            steps.Add(new AdvisorStepDraft(finding.Id, steps.Count, "assistant", null, null, "模型输出不可用"));
            return Insufficient(finding, steps, ToolStepCount(steps), "模型输出不可用");
        }

        steps.Add(new AdvisorStepDraft(finding.Id, steps.Count, "final", null, null, "生成结论"));
        return Finalize(finding, context.Chunks, answer, steps);
    }

    private static IList<AITool> BuildTools(AdvisorToolContext context) =>
    [
        AIFunctionFactory.Create(
            async (string query, CancellationToken cancellationToken) =>
            {
                var hits = await context.Knowledge.RetrieveAsync(query, 5, cancellationToken);
                var mine = hits
                    .Where(h => string.Equals(h.DocumentId, context.DocumentId, StringComparison.Ordinal))
                    .Take(5)
                    .ToList();
                return mine.Count == 0
                    ? "无命中"
                    : string.Join("\n", mine.Select(h => $"seq={h.Seq} :: {Snippet(h.Content)}"));
            },
            "retrieve_evidence",
            "在知识库中检索与 query 相关的证据片段，返回带 seq 的命中。"),

        AIFunctionFactory.Create(
            (int seq) =>
            {
                var chunk = EvidenceLocator.FindChunk(context.Chunks, null, seq);
                return chunk is null ? "无此片段" : chunk.Content;
            },
            "read_chunk",
            "读取指定 seq 的合同片段全文。"),

        AIFunctionFactory.Create(
            (string keyword) =>
            {
                var found = EvidenceLocator.Locate(context.Chunks, keyword, 3);
                return found.Count == 0
                    ? "无命中"
                    : string.Join("\n", found.Select(f => $"seq={f.Chunk.Seq} :: {f.Evidence}"));
            },
            "locate_evidence",
            "在合同文本中定位 keyword 所在的原文片段。")
    ];

    private static AdvisorLoopResult Finalize(
        ReviewFindingRecord finding,
        IReadOnlyList<ChunkRecord> chunks,
        AdvisorAnswer answer,
        List<AdvisorStepDraft> steps)
    {
        var citations = new List<AdvisorCitation>();
        foreach (var citation in answer.Citations ?? [])
        {
            if (EvidenceLocator.IsGrounded(chunks, null, citation.Seq, citation.Evidence))
            {
                var chunk = EvidenceLocator.FindChunk(chunks, null, citation.Seq)!;
                citations.Add(new AdvisorCitation(chunk.Id, citation.Seq, citation.Evidence!.Trim()));
            }
        }

        if (citations.Count == 0)
            return Insufficient(finding, steps, steps.Count, "引用不可核对");

        var primary = citations[0];
        var primaryChunk = EvidenceLocator.FindChunk(chunks, null, primary.Seq)!;
        var draft = new AdvisorConclusionDraft(
            finding.Id, ConclusionStatuses.Concluded,
            string.IsNullOrWhiteSpace(answer.Title) ? finding.Title : answer.Title!,
            answer.Conclusion, answer.Suggestion, answer.Impact,
            ConclusionBasis.Evidence, 0.8, "llm",
            primary.ChunkId, primary.Seq, primaryChunk.PageFrom, primaryChunk.PageTo,
            primary.Evidence, JsonSerializer.Serialize(citations), steps.Count);
        return new AdvisorLoopResult(draft, steps);
    }

    private static AdvisorLoopResult Insufficient(
        ReviewFindingRecord finding, List<AdvisorStepDraft> steps, int stepsUsed, string reason)
    {
        _ = reason;
        var draft = new AdvisorConclusionDraft(
            finding.Id, ConclusionStatuses.Insufficient, finding.Title,
            null, "资料不足，建议人工复核。", null,
            ConclusionBasis.NoEvidence, 0, "rule",
            null, null, null, null, null, null, stepsUsed);
        return new AdvisorLoopResult(draft, steps);
    }

    private static IReadOnlyList<FunctionCallContent> LastFunctionCalls(ChatResponse response)
    {
        var last = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        return last is null ? [] : last.Contents.OfType<FunctionCallContent>().ToList();
    }

    private static AdvisorAnswer? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return JsonSerializer.Deserialize<AdvisorAnswer>(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ToolStepCount(List<AdvisorStepDraft> steps)
        => steps.Count(s => string.Equals(s.Kind, "tool", StringComparison.Ordinal));

    private static string SerializeArguments(AIFunctionArguments arguments)
        => JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));

    private static string SerializeCall(FunctionCallContent call)
        => JsonSerializer.Serialize(call.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()) ?? []);

    private static string? Truncate(string? value)
        => value is null || value.Length <= MaxTraceChars ? value : value[..MaxTraceChars];

    private static string Snippet(string content)
        => content.Length <= 120 ? content : content[..120];
}

/// <summary>Agent Loop 工具上下文（只读）。</summary>
public sealed class AdvisorToolContext
{
    public required string DocumentId { get; init; }
    public required IReadOnlyList<ChunkRecord> Chunks { get; init; }
    public required IKnowledgeEngine Knowledge { get; init; }
}

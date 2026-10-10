using System.Text.Json;
using AgentWorkbench.Agent;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.ReviewAdvisor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentWorkbench.UnitTests.ReviewAdvisor;

public sealed class ReviewAgentLoopTests
{
    private static readonly List<ChunkRecord> Chunks =
    [
        new() { Id = 3, DocumentId = "d", Seq = 0, PageFrom = 1, PageTo = 1, Content = "任何一方违约，应向对方支付合同总价款 10% 的违约金。" }
    ];

    private static ReviewFindingRecord Finding(long id = 1) => new()
    {
        Id = id, RunId = 10, Severity = FindingSeverities.High, Category = "条款缺失",
        Title = "缺少应有条款：违约责任", Expected = "违约金"
    };

    private static AdvisorToolContext Context() => new()
    {
        DocumentId = "d", Chunks = Chunks, Knowledge = new Mock<IKnowledgeEngine>().Object
    };

    private static ReviewAgentLoop Loop(IChatClient client) =>
        new(new FakeProvider(client), NullLoggerFactory.Instance, new EmptyProvider(), NullLogger<ReviewAgentLoop>.Instance);

    private static string FinalJson(int seq, string evidence) => JsonSerializer.Serialize(new
    {
        title = "违约责任缺失",
        conclusion = "合同未约定违约金。",
        suggestion = "建议补充违约金条款。",
        impact = "条款层面存在不一致。",
        citations = new[] { new { seq, evidence } }
    });

    private static ChatResponse ToolCall(string name, IDictionary<string, object?>? args = null)
    {
        var call = new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args ?? new Dictionary<string, object?>());
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }));
    }

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public async Task Run_DirectFinalWithGroundedCitation_Concluded()
    {
        var client = new ScriptedChatClient(Text(FinalJson(0, "违约金")));
        var result = await Loop(client).RunAsync(Finding(), Context(), [], 6, CancellationToken.None);

        Assert.Equal(ConclusionStatuses.Concluded, result.Conclusion.Status);
        Assert.Equal(3, result.Conclusion.ChunkId);
        Assert.Equal(0, result.Conclusion.Seq);
        Assert.NotNull(result.Conclusion.Citations);
    }

    [Fact]
    public async Task Run_FinalWithUngroundedCitation_Insufficient()
    {
        var client = new ScriptedChatClient(Text(FinalJson(0, "不存在的片段")));
        var result = await Loop(client).RunAsync(Finding(), Context(), [], 6, CancellationToken.None);

        Assert.Equal(ConclusionStatuses.Insufficient, result.Conclusion.Status);
        Assert.Equal(ConclusionBasis.NoEvidence, result.Conclusion.Basis);
    }

    [Fact]
    public async Task Run_ToolThenFinal_RecordsToolStep()
    {
        var client = new ScriptedChatClient(
            ToolCall("locate_evidence", new Dictionary<string, object?> { ["keyword"] = "违约" }),
            Text(FinalJson(0, "违约金")));

        var result = await Loop(client).RunAsync(Finding(), Context(), [], 6, CancellationToken.None);

        Assert.Equal(ConclusionStatuses.Concluded, result.Conclusion.Status);
        Assert.Contains(result.Steps, s => s.Kind == "tool" && s.Tool == "locate_evidence");
    }

    [Fact]
    public async Task Run_AlwaysTool_StopsAtMaxSteps_Insufficient()
    {
        var responses = Enumerable.Range(0, 10)
            .Select(_ => (Func<ChatResponse>)(() => ToolCall("locate_evidence", new Dictionary<string, object?> { ["keyword"] = "违约" })))
            .ToArray();
        var client = new ScriptedChatClient(responses);

        var result = await Loop(client).RunAsync(Finding(), Context(), [], 2, CancellationToken.None);

        Assert.Equal(ConclusionStatuses.Insufficient, result.Conclusion.Status);
        Assert.Equal(2, result.Conclusion.StepsUsed);
        Assert.Equal(3, result.Steps.Count); // 2 tool steps + 1 终止步
    }

    [Fact]
    public async Task Run_UnknownTool_RejectedAndNotExecuted()
    {
        var client = new ScriptedChatClient(ToolCall("drop_table"));

        var result = await Loop(client).RunAsync(Finding(), Context(), [], 6, CancellationToken.None);

        var rejected = Assert.Single(result.Steps, s => s.Tool == "drop_table");
        Assert.Contains("拒绝", rejected.Result);
        Assert.Equal(ConclusionStatuses.Insufficient, result.Conclusion.Status);
    }

    [Fact]
    public async Task Run_ModelReturnsEmpty_InsufficientWithoutThrowing()
    {
        var client = new ScriptedChatClient(Text(string.Empty));

        var result = await Loop(client).RunAsync(Finding(), Context(), [], 6, CancellationToken.None);

        Assert.Equal(ConclusionStatuses.Insufficient, result.Conclusion.Status);
    }

    [Fact]
    public async Task Run_ModelNotConfigured_Insufficient()
    {
        var loop = new ReviewAgentLoop(
            new FakeProvider(null), NullLoggerFactory.Instance, new EmptyProvider(), NullLogger<ReviewAgentLoop>.Instance);

        var result = await loop.RunAsync(Finding(), Context(), [], 6, CancellationToken.None);

        Assert.Equal(ConclusionStatuses.Insufficient, result.Conclusion.Status);
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<ChatResponse>> _responses;
        private readonly Func<ChatResponse> _fallback;

        public ScriptedChatClient(params ChatResponse[] responses)
            : this(responses.Select<ChatResponse, Func<ChatResponse>>(r => () => r).ToArray())
        {
        }

        public ScriptedChatClient(params Func<ChatResponse>[] responses)
        {
            _responses = new Queue<Func<ChatResponse>>(responses);
            _fallback = () => Text(string.Empty);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var next = _responses.Count > 0 ? _responses.Dequeue() : _fallback;
            return Task.FromResult(next());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeProvider(IChatClient? client) : IChatClientProvider
    {
        public Task<IChatClient?> GetClientAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(client);
    }

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

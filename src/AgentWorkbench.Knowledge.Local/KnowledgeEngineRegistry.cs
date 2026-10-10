using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>按配置解析知识引擎；未实现的引擎降级为不可用 stub。</summary>
public sealed class KnowledgeEngineRegistry
{
    private readonly IKnowledgeEngine _local;
    private readonly KnowledgeOptions _options;

    public KnowledgeEngineRegistry(LocalKnowledgeEngine local, IOptions<KnowledgeOptions> options)
    {
        _local = local;
        _options = options.Value;
    }

    public IKnowledgeEngine Resolve() => _options.Provider?.Trim().ToLowerInvariant() switch
    {
        "local" or "" or null => _local,
        var other => new UnavailableKnowledgeEngine(other)
    };
}

/// <summary>未接入引擎（anythingllm / ragflow / opencontracts）的占位实现。</summary>
public sealed class UnavailableKnowledgeEngine : IKnowledgeEngine
{
    public UnavailableKnowledgeEngine(string provider) => Provider = provider;

    public string Provider { get; }

    public KnowledgeCapabilities Capabilities => new(false, false, false, false);

    public Task<KnowledgeStatus> GetStatusAsync(CancellationToken cancellationToken)
        => Task.FromResult(new KnowledgeStatus(Provider, Capabilities, false, "none", string.Empty, 0, 0));

    public Task IndexDocumentAsync(string documentId, CancellationToken cancellationToken)
        => throw new NotImplementedException($"知识引擎 '{Provider}' 尚未接入（仅 local 已实现）。");

    public Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken)
        => throw new NotImplementedException($"知识引擎 '{Provider}' 尚未接入（仅 local 已实现）。");

    public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, int topK, CancellationToken cancellationToken)
        => throw new NotImplementedException($"知识引擎 '{Provider}' 尚未接入（仅 local 已实现）。");

    public IAsyncEnumerable<string> StreamAnswerAsync(
        string query, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
        => throw new NotImplementedException($"知识引擎 '{Provider}' 尚未接入（仅 local 已实现）。");
}

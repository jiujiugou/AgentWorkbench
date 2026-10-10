using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Knowledge.Local.Embedding;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge;

/// <summary>Knowledge 模块（P1 知识库）：Embedding + sqlite-vec + FTS5 混合检索 + 带引用问答。</summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddNitroKnowledge(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KnowledgeOptions>().Bind(configuration.GetSection(KnowledgeOptions.SectionName));
        services.AddHttpClient();

        services.AddSingleton<IEmbeddingProvider, EmbeddingProviderRegistry>();
        services.AddSingleton(sp => new SqliteVectorStore(sp.GetRequiredService<StoragePaths>().ConnectionString));
        services.AddSingleton<VectorIndex>();
        services.AddSingleton<ChunkIndexer>();
        services.AddSingleton<IReranker, NullReranker>();
        services.AddSingleton<HybridRetriever>();
        services.AddSingleton<LocalKnowledgeEngine>();
        services.AddSingleton<IKnowledgeEngine>(sp => new KnowledgeEngineRegistry(
            sp.GetRequiredService<LocalKnowledgeEngine>(),
            sp.GetRequiredService<IOptions<KnowledgeOptions>>()).Resolve());
        services.AddSingleton<IRagService, RagService>();
        return services;
    }
}

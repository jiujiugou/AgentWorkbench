using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Embedding;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Knowledge.Local.Tools;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.ToolExecution;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>
/// 知识库模块（P1）自注册入口。
/// 原独立 <c>AgentWorkbench.Knowledge</c> 项目只装这一行注册，阶段 4 砍伐并入本地实现
/// （合并的是项目边界，注册签名 <c>AddNitroKnowledge</c> 与装配内容一字未动）。
/// </summary>
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

        // 知识库工具：由知识模块自己注册（工具平台不反向依赖知识库）
        services.AddSingleton<IAgentToolProvider, SearchKnowledgeTool>();
        return services;
    }
}

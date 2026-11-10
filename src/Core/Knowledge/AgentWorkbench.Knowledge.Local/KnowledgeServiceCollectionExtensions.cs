using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Embedding;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared.Options;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>
/// 知识库模块自注册入口。<b>只装配索引写入</b>：检索执行已移到 Python MCP server，
/// 由 <c>AddNitroMcp</c> 拉起的 <c>mcp__rag__search_knowledge</c> 承担。
/// </summary>
/// <remarks>
/// 写入留在 C#：文档上传 → 解析 → 切片 → 写 FTS / 向量全在本进程，Python server 以
/// <c>mode=ro</c> 只读同一个库。两边共用一份索引，才不会出现「C# 写了、Python 查不到」
/// 或「重建索引后一边新鲜一边过期」的漂移。
/// 引用判定（<c>CitationVerifier</c> / <c>SufficiencyGate</c>）也留在 C#，由 Host 直接调用，
/// 不经 DI——判定是判据，不是可替换的服务。
/// </remarks>
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
        services.AddSingleton<LocalKnowledgeEngine>();
        services.AddSingleton<IKnowledgeEngine>(sp => new KnowledgeEngineRegistry(
            sp.GetRequiredService<LocalKnowledgeEngine>(),
            sp.GetRequiredService<IOptions<KnowledgeOptions>>()).Resolve());

        // 结果否决：MCP 检索工具零命中时终止生成。判定留在 C#，不在 Python server 里做。
        // 管道只认识接口，不认识「知识库」——领域判断留在领域模块。
        services.AddSingleton<IToolResultVeto, McpKnowledgeVeto>();
        return services;
    }
}

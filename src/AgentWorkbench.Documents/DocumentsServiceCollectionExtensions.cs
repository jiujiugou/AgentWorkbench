using AgentWorkbench.Documents.Parsing;
using AgentWorkbench.Documents.Tools;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.Documents;

/// <summary>Documents 模块（P0 数据管道）：上传 → 队列 → 后台解析 → 清洗 → 切分。</summary>
public static class DocumentsServiceCollectionExtensions
{
    public static IServiceCollection AddNitroDocuments(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDocumentParseQueue, DocumentParseQueue>();
        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextCleaner, TextCleaner>();
        services.AddSingleton<IChunker, Chunker>();
        services.AddSingleton<DocumentIngestionService>();
        services.AddHostedService<DocumentParseWorker>();

        // 文档工具：由文档模块自己注册（工具平台不反向依赖文档仓储）
        services.AddSingleton<IAgentToolProvider, ListDocumentsTool>();
        return services;
    }
}

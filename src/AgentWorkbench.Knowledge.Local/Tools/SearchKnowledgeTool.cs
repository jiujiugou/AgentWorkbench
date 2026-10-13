using System.Text.Json;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Knowledge.Local.Tools;

/// <summary>
/// 工具 <c>search_knowledge</c>：在本地知识库检索与 query 相关的原文片段（带来源）。
/// 放在知识模块内，避免工具平台（ToolExecution）反向依赖知识库。
/// </summary>
/// <remarks>
/// 返回<b>结构化 JSON 数组</b>（字段同 <c>RetrievalHit</c>）而不是拼好的文本：同一份结果有两个消费者——
/// 模型（据此作答并标注出处，见 <see cref="KnowledgeInstructions"/>）与界面来源面板
/// （<c>AgentHub</c> 解析同一份 JSON 后推 <c>ReceiveSources</c>）。
/// 拼成人类可读文本给模型，界面就得再把文本解析回来；JSON 一次成型，两边都能直接用。
/// </remarks>
public sealed class SearchKnowledgeTool : IAgentToolProvider
{
    public const string ToolName = "search_knowledge";

    /// <summary>工具输出的序列化约定（Web 默认：camelCase + 大小写不敏感）；调用方按同一约定解析。</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const int TopK = 5;

    private readonly IServiceProvider _services;

    // 惰性解析：IKnowledgeEngine → LocalKnowledgeEngine → IRagService → IChatService，
    // 构造期直接注入会与 ChatService 形成 DI 循环。
    public SearchKnowledgeTool(IServiceProvider services) => _services = services;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            async (string query, CancellationToken cancellationToken) =>
            {
                var knowledge = _services.GetRequiredService<IKnowledgeEngine>();
                var hits = await knowledge.RetrieveAsync(query, TopK, cancellationToken);

                // 无命中时返回空数组（而不是一句「无命中」）：模型看到 [] 就知道「资料里没有」，
                // 与「工具本身没跑起来 / 解析失败」区分得开。
                return JsonSerializer.Serialize(hits, SerializerOptions);
            },
            ToolName,
            "在本地知识库中检索与 query 相关的原文片段，返回结构化 JSON 命中数组"
            + "（字段：fileName / seq / heading / pageFrom / pageTo / content）。")
    ];
}

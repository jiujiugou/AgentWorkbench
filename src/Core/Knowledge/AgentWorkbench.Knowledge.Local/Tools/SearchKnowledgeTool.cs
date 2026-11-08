using System.Text.Json;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Local.Sufficiency;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Tools.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local.Tools;

/// <summary>
/// 工具 <c>search_knowledge</c>：在本地知识库检索与 query 相关的原文片段（带来源）。
/// 放在知识模块内，避免工具平台（ToolExecution）反向依赖知识库。
/// </summary>
/// <remarks>
/// 返回<b>结构化 JSON</b>（<c>hits</c> 与 <c>sufficiency</c> 两段）而不是拼好的文本：同一份结果有三个消费者——
/// 模型（据此作答并标注出处，见 <see cref="KnowledgeInstructions"/>）、界面来源面板
/// （<c>AgentHub</c> 解析同一份 JSON 后推 <c>ReceiveSources</c>）、以及否决层（读 <c>sufficiency</c> 决定要不要终止生成）。
/// 拼成人类可读文本给模型，界面就得再把文本解析回来；JSON 一次成型，三边都能直接用。
/// </remarks>
public sealed class SearchKnowledgeTool : IAgentToolProvider
{
    public const string ToolName = "search_knowledge";

    /// <summary>工具输出的序列化约定（Web 默认：camelCase + 大小写不敏感）；调用方按同一约定解析。</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

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
                var options = _services.GetRequiredService<IOptions<KnowledgeOptions>>().Value;

                // 条数取自配置，不写死在工具里：五块全文一起进上下文，最长那块两千多字，
                // 一条问题可能喂进去上万字——条数是上下文预算的一部分。
                var hits = await knowledge.RetrieveAsync(query, Math.Max(1, options.TopK), cancellationToken);

                // 判定只算一次，结果随命中一起返回。否决层读这个字段，不再重算——
                // 判据在两处各实现一遍，迟早漂移，而且漂移时不会报错。
                var sufficiency = SufficiencyGate.Judge(
                    hits, new SufficiencyOptions(options.RefuseMargin, options.SufficientDistance));

                // 无命中时 hits 是空数组（而不是一句「无命中」）：模型看到 [] 就知道「资料里没有」，
                // 与「工具本身没跑起来 / 解析失败」区分得开。
                //
                // 每条带一个 no（从 1 起）：它就是模型在答案里写的 [n]，也是引用校验的编号空间。
                // 编号在工具输出这一刻固定，服务端事后按同一顺序还原，不需要额外存储。
                var numbered = hits.Select((hit, index) => new NumberedHit
                {
                    No = index + 1,
                    ChunkId = hit.ChunkId,
                    DocumentId = hit.DocumentId,
                    FileName = hit.FileName,
                    Seq = hit.Seq,
                    Heading = hit.Heading,
                    PageFrom = hit.PageFrom,
                    PageTo = hit.PageTo,
                    Content = hit.Content,
                    VectorScore = hit.VectorScore,
                    TextScore = hit.TextScore,
                    FusedScore = hit.FusedScore
                }).ToList();

                // 「内容被截断」那条子问题只有一个观测口：本轮到底送进去多少字。
                // 不记这个数，截断不会报错——用户只看到答案少了一半，而日志里什么都查不到。
                var contextChars = ContextCharsOf(numbered);
                ReportContextSize(_services, contextChars, options.ContextCharBudget, numbered.Count);

                return JsonSerializer.Serialize(
                    new Payload
                    {
                        Hits = numbered,
                        ContextChars = contextChars,
                        Sufficiency = sufficiency
                    }, SerializerOptions);
            },
            ToolName,
            "在本地知识库中检索与 query 相关的原文片段，返回结构化 JSON"
            + "（hits 的字段：no / fileName / seq / heading / pageFrom / pageTo / content；sufficiency 表示本次检索够不够作答）。"
            + "no 是该片段的编号，引用它时写成 [no]。")
    ];

    /// <summary>本轮送进模型的资料字符数：五块 content 之和。</summary>
    /// <remarks>
    /// 按 <see cref="string.Length"/>（UTF-16 码元）计，不按字节：模型窗口按 token 算，
    /// 但拿不到精确 token 数时，字符数是唯一自己能算、也能被别人复算的量。
    /// 单独抽出来是为了能直接单测——它的消费者是「内容被截断」那条判据，不是界面。
    /// </remarks>
    public static int ContextCharsOf(IEnumerable<NumberedHit> hits) => hits.Sum(h => h.Content.Length);

    /// <summary>是否超出资料预算。<c>null</c> 表示还没定标，永不告警。</summary>
    public static bool ExceedsContextBudget(int contextChars, int? budget) =>
        budget is int limit && contextChars > limit;

    private static void ReportContextSize(
        IServiceProvider services, int contextChars, int? budget, int hitCount)
    {
        var logger = services.GetService<ILogger<SearchKnowledgeTool>>();
        if (logger is null)
        {
            return;
        }

        if (ExceedsContextBudget(contextChars, budget))
        {
            logger.LogWarning(
                "search_knowledge 本轮资料 {Chars} 字（{Hits} 块），超过预算 {Budget} 字；超出部分会被截断，用户看到的答案可能少一半。",
                contextChars, hitCount, budget);
            return;
        }

        logger.LogInformation(
            "search_knowledge 本轮资料 {Chars} 字（{Hits} 块）。", contextChars, hitCount);
    }

    /// <summary>一次检索的完整输出：命中列表 + 够不够的判定。</summary>
    /// <remarks>
    /// 两段必须出自同一次检索。分成两次调用就一定会有「判定针对 A 批、模型拿的是 B 批」的窗口，
    /// 而这种错不会报错。
    /// </remarks>
    public sealed record Payload
    {
        public List<NumberedHit> Hits { get; init; } = [];

        /// <summary>本轮资料字符数，与 <see cref="Hits"/> 出自同一份结果。</summary>
        public int ContextChars { get; init; }

        public SufficiencyResult Sufficiency { get; init; } =
            new(SufficiencyVerdict.Undetermined, null, null, "not_judged");
    }

    /// <summary>带编号的命中；编号是模型写 <c>[n]</c> 的依据，也是服务端还原的顺序。</summary>
    public sealed record NumberedHit
    {
        public int No { get; init; }
        public long ChunkId { get; init; }
        public string DocumentId { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public int Seq { get; init; }
        public string? Heading { get; init; }
        public int PageFrom { get; init; }
        public int PageTo { get; init; }
        public string Content { get; init; } = string.Empty;
        public double? VectorScore { get; init; }
        public double? TextScore { get; init; }
        public double FusedScore { get; init; }
    }
}

using System.Text.Json.Serialization;
using AgentWorkbench.Agent;

namespace AgentWorkbench.Review.Extraction;

/// <summary>LLM 增强抽取：配置模型后启用，用结构化 JSON 覆盖/补充规则抽取。</summary>
public sealed class LlmElementExtractor : IElementExtractor
{
    private const int MaxChars = 12000;

    private readonly IStructuredChatService _chat;
    private readonly ILogger<LlmElementExtractor> _logger;

    public LlmElementExtractor(IStructuredChatService chat, ILogger<LlmElementExtractor> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public string Name => "llm";

    public async Task<IReadOnlyList<ElementDraft>> ExtractAsync(ReviewContext context, CancellationToken cancellationToken)
    {
        if (!await _chat.IsConfiguredAsync(cancellationToken))
        {
            _logger.LogDebug("LLM 未配置，跳过增强抽取");
            return [];
        }

        var text = context.FullText;
        if (text.Length > MaxChars)
            text = text[..MaxChars];

        var system = "你是严谨的合同信息抽取助手。仅依据给定合同文本抽取要素，找不到则留空，不得编造。";
        var user =
            "请从下面的合同文本中抽取要素，返回 JSON：\n" +
            "{\"seller\":\"\",\"buyer\":\"\",\"amount\":\"\",\"amount_text\":\"\",\"delivery_date\":\"\"," +
            "\"warranty_months\":\"\",\"acceptance_days\":\"\",\"penalty_rate\":\"\",\"subject\":\"\"}\n\n" +
            "约定：amount 只填阿拉伯数字（不含千分位）；penalty_rate 只填百分数数字；日期用 YYYY-MM-DD；" +
            "warranty_months 单位月；acceptance_days 单位日。\n\n【合同文本】\n" + text;

        LlmElements? result;
        try
        {
            result = await _chat.GetJsonAsync<LlmElements>(system, user, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LLM 要素抽取失败，降级为规则抽取结果");
            return [];
        }

        if (result is null)
            return [];

        var drafts = new List<ElementDraft>();
        Add(drafts, context, "seller", "出卖人", result.Seller, "text", null);
        Add(drafts, context, "buyer", "买受人", result.Buyer, "text", null);
        Add(drafts, context, "amount", "合同总价", result.Amount, "money", "元");
        Add(drafts, context, "amount_text", "金额大写", result.AmountText, "text", null);
        Add(drafts, context, "delivery_date", "交付期限", result.DeliveryDate, "date", null);
        Add(drafts, context, "warranty_months", "质保期", result.WarrantyMonths, "number", "个月");
        Add(drafts, context, "acceptance_days", "验收期限", result.AcceptanceDays, "number", "日");
        Add(drafts, context, "penalty_rate", "违约金比例", result.PenaltyRate, "percent", "%");
        Add(drafts, context, "subject", "标的物", result.Subject, "text", null);

        _logger.LogInformation("LLM 要素抽取完成：{Count} 项", drafts.Count);
        return drafts;
    }

    private static void Add(
        List<ElementDraft> drafts, ReviewContext context, string key, string label,
        string? value, string valueType, string? unit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var trimmed = value.Trim();
        var (chunkId, seq, pageFrom, pageTo, evidence) = Locate(context, trimmed);
        drafts.Add(new ElementDraft(key, label, trimmed, valueType, unit, "llm", 0.8,
            chunkId, seq, pageFrom, pageTo, evidence));
    }

    private static (long? chunkId, int? seq, int? pageFrom, int? pageTo, string? evidence) Locate(ReviewContext context, string value)
    {
        foreach (var chunk in context.Chunks)
        {
            var idx = chunk.Content.IndexOf(value, StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var start = Math.Max(0, idx - 24);
            var length = Math.Min(chunk.Content.Length - start, value.Length + 48);
            return (chunk.Id, chunk.Seq, chunk.PageFrom, chunk.PageTo, chunk.Content.Substring(start, length).Trim());
        }
        return (null, null, null, null, null);
    }

    private sealed class LlmElements
    {
        [JsonPropertyName("seller")] public string? Seller { get; set; }
        [JsonPropertyName("buyer")] public string? Buyer { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("amount_text")] public string? AmountText { get; set; }
        [JsonPropertyName("delivery_date")] public string? DeliveryDate { get; set; }
        [JsonPropertyName("warranty_months")] public string? WarrantyMonths { get; set; }
        [JsonPropertyName("acceptance_days")] public string? AcceptanceDays { get; set; }
        [JsonPropertyName("penalty_rate")] public string? PenaltyRate { get; set; }
        [JsonPropertyName("subject")] public string? Subject { get; set; }
    }
}

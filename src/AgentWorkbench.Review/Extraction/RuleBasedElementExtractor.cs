using System.Text.RegularExpressions;

namespace AgentWorkbench.Review.Extraction;

/// <summary>确定性要素抽取：正则 + 关键词，无外部依赖，模型不可用时仍可产出。</summary>
public sealed class RuleBasedElementExtractor : IElementExtractor
{
    public string Name => "rule";

    public Task<IReadOnlyList<ElementDraft>> ExtractAsync(ReviewContext context, CancellationToken cancellationToken)
    {
        var drafts = new List<ElementDraft>();

        // 当事人
        AddParty(drafts, context, "seller", "出卖人",
            new Regex(@"(?:出卖人|卖方|供方|出租人)\s*(?:[（(][^）)]{0,6}[）)])?\s*[:：]\s*(.{2,40}?)(?=\s|买受人|出卖人|乙方|甲方|承租人|出租人|供方|需方|法定代表人|[\r\n]|$)"));
        AddParty(drafts, context, "buyer", "买受人",
            new Regex(@"(?:买受人|买方|需方|承租人)\s*(?:[（(][^）)]{0,6}[）)])?\s*[:：]\s*(.{2,40}?)(?=\s|买受人|出卖人|乙方|甲方|承租人|出租人|供方|需方|法定代表人|[\r\n]|$)"));

        // 金额（数字）与中文大写
        AddFromRegex(drafts, context, "amount", "合同总价", "money", "元", 0.9,
            new Regex(@"(?:合同总价款|总价款|合同金额|价款|总价|金额)\s*(?:为|是)?\s*(?:人民币)?\s*([0-9][0-9,，]*(?:\.[0-9]+)?)\s*元"));
        AddFromRegex(drafts, context, "amount_text", "金额大写", "text", null, 0.85,
            new Regex(@"[（(]\s*([零壹贰叁肆伍陆柒捌玖拾佰仟万亿元整角分两]+)\s*[)）]"));

        // 期限：交付日期 / 质保 / 验收
        AddFromRegex(drafts, context, "delivery_date", "交付期限", "date", null, 0.85,
            new Regex(@"(?:交货|交付|应于|定于|于)[^0-9\r\n]{0,10}([0-9]{4}\s*年\s*[0-9]{1,2}\s*月\s*[0-9]{1,2}\s*日)"));
        AddFromRegex(drafts, context, "warranty_months", "质保期", "number", "个月", 0.85,
            new Regex(@"(?:质保|保修|质量保证)[^0-9\r\n]{0,12}([0-9]+)\s*个月"));
        AddFromRegex(drafts, context, "acceptance_days", "验收期限", "number", "日", 0.8,
            new Regex(@"(?:收到货物后|验收|异议)[^0-9\r\n]{0,8}([0-9]+)\s*(?:日|天)"));

        // 违约金比例
        AddFromRegex(drafts, context, "penalty_rate", "违约金比例", "percent", "%", 0.85,
            new Regex(@"(?:违约金|违约)[^%。\r\n]{0,40}?([0-9]+(?:\.[0-9]+)?)\s*%"));

        // 标的物
        AddFromRegex(drafts, context, "subject", "标的物", "text", null, 0.5,
            new Regex(@"(?:标的物|出售|供应|买卖)[^\r\n，,。；;]{0,40}"));

        return Task.FromResult<IReadOnlyList<ElementDraft>>(drafts);
    }

    private static void AddParty(List<ElementDraft> drafts, ReviewContext context, string key, string label, Regex regex)
    {
        foreach (var chunk in context.Chunks)
        {
            var match = regex.Match(chunk.Content);
            if (!match.Success)
                continue;
            var value = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            drafts.Add(new ElementDraft(key, label, value, "text", null, "rule", 0.7,
                chunk.Id, chunk.Seq, chunk.PageFrom, chunk.PageTo, Snippet(chunk.Content, value)));
            return;
        }
    }

    private static void AddFromRegex(
        List<ElementDraft> drafts, ReviewContext context, string key, string label,
        string valueType, string? unit, double confidence, Regex regex)
    {
        foreach (var chunk in context.Chunks)
        {
            var match = regex.Match(chunk.Content);
            if (!match.Success)
                continue;
            var value = match.Groups[1].Value.Trim().Replace(",", string.Empty).Replace("，", string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            drafts.Add(new ElementDraft(key, label, value, valueType, unit, "rule", confidence,
                chunk.Id, chunk.Seq, chunk.PageFrom, chunk.PageTo, Snippet(chunk.Content, match.Groups[1].Value.Trim())));
            return;
        }
    }

    private static string Snippet(string content, string value)
    {
        var idx = content.IndexOf(value, StringComparison.Ordinal);
        if (idx < 0)
            return value;
        var start = Math.Max(0, idx - 24);
        var length = Math.Min(content.Length - start, value.Length + 48);
        return content.Substring(start, length).Trim();
    }
}

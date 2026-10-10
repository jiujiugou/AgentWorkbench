namespace AgentWorkbench.Review.Comparison;

/// <summary>必备要素缺失检查：模板要求但未抽取到。</summary>
public sealed class RequiredElementChecker : IRuleChecker
{
    public string Name => "required-element";

    public IEnumerable<FindingDraft> Check(
        ReviewContext context, IReadOnlyList<RuleRecord> rules, IReadOnlyList<ElementDraft> elements)
    {
        foreach (var rule in rules.Where(r => r.RuleType == RuleTypes.ElementRequired))
        {
            var key = rule.TargetKey ?? rule.Name;
            var found = elements.FirstOrDefault(e =>
                string.Equals(e.Key, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(e.Value));
            if (found is not null)
                continue;

            yield return new FindingDraft(
                rule.Id, rule.Severity, "要素缺失",
                $"缺少必备要素：{rule.Name}",
                string.IsNullOrWhiteSpace(rule.Body) ? "模板要求该要素必须存在，但未从合同文本中抽取到。" : rule.Body,
                rule.Expected, null, null, null, null, null, null);
        }
    }
}

/// <summary>条款存在性检查：应有条款缺失 / 禁止条款出现。</summary>
public sealed class ClausePresenceChecker : IRuleChecker
{
    public string Name => "clause-presence";

    public IEnumerable<FindingDraft> Check(
        ReviewContext context, IReadOnlyList<RuleRecord> rules, IReadOnlyList<ElementDraft> elements)
    {
        foreach (var rule in rules)
        {
            var keyword = string.IsNullOrWhiteSpace(rule.MatchHint) ? rule.Name : rule.MatchHint!;
            var hit = Locate(context, keyword);

            if (rule.RuleType == RuleTypes.ClauseRequired && hit is null)
            {
                yield return new FindingDraft(
                    rule.Id, rule.Severity, "条款缺失",
                    $"缺少应有条款：{rule.Name}",
                    rule.Body, rule.Expected, null,
                    null, null, null, null, null);
            }
            else if (rule.RuleType == RuleTypes.ClauseForbidden && hit is not null)
            {
                var (chunk, evidence) = hit.Value;
                yield return new FindingDraft(
                    rule.Id, rule.Severity, "风险条款",
                    $"存在禁止/风险条款：{rule.Name}",
                    rule.Body, null, keyword,
                    chunk.Id, chunk.Seq, chunk.PageFrom, chunk.PageTo, evidence);
            }
        }
    }

    private static (ChunkRecord chunk, string evidence)? Locate(ReviewContext context, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return null;
        foreach (var chunk in context.Chunks)
        {
            var idx = chunk.Content.IndexOf(keyword, StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var start = Math.Max(0, idx - 24);
            var length = Math.Min(chunk.Content.Length - start, keyword.Length + 48);
            return (chunk, chunk.Content.Substring(start, length).Trim());
        }
        return null;
    }
}

/// <summary>取值一致性检查：期望值不符 / 金额大小写不一致。</summary>
public sealed class ValueConsistencyChecker : IRuleChecker
{
    public string Name => "value-consistency";

    public IEnumerable<FindingDraft> Check(
        ReviewContext context, IReadOnlyList<RuleRecord> rules, IReadOnlyList<ElementDraft> elements)
    {
        // 规则驱动的期望值比对
        foreach (var rule in rules.Where(r => r.RuleType == RuleTypes.ValueRule && !string.IsNullOrWhiteSpace(r.TargetKey)))
        {
            var element = elements.FirstOrDefault(e => string.Equals(e.Key, rule.TargetKey, StringComparison.Ordinal));
            if (element is null || string.IsNullOrWhiteSpace(element.Value) || string.IsNullOrWhiteSpace(rule.Expected))
                continue;
            if (string.Equals(element.Value.Trim(), rule.Expected!.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new FindingDraft(
                rule.Id, rule.Severity, "取值不一致",
                $"{rule.Name}与标准不一致",
                rule.Body, rule.Expected, element.Value,
                element.ChunkId, element.Seq, element.PageFrom, element.PageTo, element.Evidence);
        }

        // 内置：金额阿拉伯数字 vs 中文大写
        var amount = elements.FirstOrDefault(e => e.Key == "amount");
        var amountText = elements.FirstOrDefault(e => e.Key == "amount_text");
        if (amount is not null && amountText is not null
            && long.TryParse(amount.Value, out var numeric)
            && ChineseNumber.TryParse(amountText.Value, out var chinese)
            && numeric != chinese)
        {
            yield return new FindingDraft(
                null, FindingSeverities.High, "金额不一致",
                "合同金额大小写不一致",
                $"阿拉伯数字金额 {numeric} 与中文大写金额 {chinese} 不一致。",
                numeric.ToString(), chinese.ToString(),
                amountText.ChunkId, amountText.Seq, amountText.PageFrom, amountText.PageTo, amountText.Evidence);
        }
    }
}

namespace AgentWorkbench.Review.Comparison;

/// <summary>文档相关性：非合同文本不套用合同规则，避免对无关文档产生高危且无证据的清单。</summary>
public static class DocumentRelevance
{
    private static readonly string[] ContractSignals =
    [
        "合同", "协议", "甲方", "乙方", "出卖人", "买受人", "供方", "需方", "承租", "出租", "价款", "标的"
    ];

    public static bool LooksLikeContract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return ContractSignals.Any(s => text.Contains(s, StringComparison.Ordinal));
    }
}

using AgentWorkbench.Review.Comparison;
using AgentWorkbench.Review.Extraction;

namespace AgentWorkbench.Review;

/// <summary>Review 模块（P2 要素抽取与比对）：抽取器 / 检查器 / 报告 / 后台执行。</summary>
public static class ReviewServiceCollectionExtensions
{
    public static IServiceCollection AddNitroReview(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IReviewRunQueue, ReviewRunQueue>();

        services.AddSingleton<IElementExtractor, RuleBasedElementExtractor>();
        services.AddSingleton<IElementExtractor, LlmElementExtractor>();
        services.AddSingleton<ElementExtractorRegistry>();

        services.AddSingleton<IRuleChecker, RequiredElementChecker>();
        services.AddSingleton<IRuleChecker, ClausePresenceChecker>();
        services.AddSingleton<IRuleChecker, ValueConsistencyChecker>();
        services.AddSingleton<RuleCheckerRegistry>();

        services.AddSingleton<IReviewReportWriter, ReviewReportWriter>();
        services.AddSingleton<IReviewProgressNotifier, NullReviewProgressNotifier>();
        services.AddSingleton<IReviewService, ReviewService>();
        services.AddHostedService<ReviewWorker>();
        return services;
    }

    /// <summary>首次运行：幂等种子一批买卖合同审查清单（规则库内容 P2 填充）。</summary>
    public static async Task SeedReviewRulesAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IRuleRepository>();
        var existing = await rules.ListAsync();
        var names = existing
            .Where(r => string.Equals(r.Category, "买卖合同", StringComparison.Ordinal))
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var rule in StarterRules.Where(r => !names.Contains(r.Name)))
            await rules.InsertAsync(rule);
    }

    private static readonly RuleRecord[] StarterRules =
    [
        new() { Category = "买卖合同", Name = "合同总价", Body = "合同应明确约定总价款。", RuleType = RuleTypes.ElementRequired, Severity = FindingSeverities.High, TargetKey = "amount" },
        new() { Category = "买卖合同", Name = "出卖人", Body = "合同应载明出卖人（甲方）。", RuleType = RuleTypes.ElementRequired, Severity = FindingSeverities.Medium, TargetKey = "seller" },
        new() { Category = "买卖合同", Name = "买受人", Body = "合同应载明买受人（乙方）。", RuleType = RuleTypes.ElementRequired, Severity = FindingSeverities.Medium, TargetKey = "buyer" },
        new() { Category = "买卖合同", Name = "交付期限", Body = "合同应约定交货/交付期限。", RuleType = RuleTypes.ElementRequired, Severity = FindingSeverities.Medium, TargetKey = "delivery_date" },
        new() { Category = "买卖合同", Name = "违约责任", Body = "合同应约定违约责任（含违约金）。", RuleType = RuleTypes.ClauseRequired, Severity = FindingSeverities.High, MatchHint = "违约" },
        new() { Category = "买卖合同", Name = "质量保证", Body = "合同应约定质量保证 / 质保期。", RuleType = RuleTypes.ClauseRequired, Severity = FindingSeverities.Medium, MatchHint = "质保" },
        new() { Category = "买卖合同", Name = "争议解决", Body = "合同应约定争议解决方式。", RuleType = RuleTypes.ClauseRequired, Severity = FindingSeverities.Low, MatchHint = "争议" }
    ];
}

using AgentWorkbench.Knowledge.Abstractions.Retrieval;

namespace AgentWorkbench.Knowledge.Local.Sufficiency;

/// <summary>
/// 判定结论的取值（四个）。「失败」不在这里——它是系统状态，不由判定产生。
/// </summary>
public static class SufficiencyVerdict
{
    /// <summary>有明确赢家，且足够近。</summary>
    public const string Answered = "answered";

    /// <summary>答，但依据不硬：够近却没有赢家，或只有一条参照。</summary>
    public const string Weak = "weak";

    /// <summary>判不足。交否决层处理，不由模型自己决定。</summary>
    public const string Insufficient = "insufficient";

    /// <summary>没有能力判断（缺向量 / 阈值未配）。必须显示出来，不能混进「已回答」。</summary>
    public const string Undetermined = "undetermined";
}

/// <summary>
/// 结论的原因码。只在 <see cref="SufficiencyVerdict.Weak"/> 与 <see cref="SufficiencyVerdict.Undetermined"/> 上有值。
/// </summary>
/// <remarks>
/// 界面文案要区分「没配阈值」和「配了但判不了」，只有结论值区分不出来——两种都是「未判定」。
/// </remarks>
public static class SufficiencyReason
{
    /// <summary>一条候选都没有。</summary>
    public const string NoCandidates = "no_candidates";

    /// <summary>候选全来自全文路，没有距离可用。</summary>
    public const string NoVector = "no_vector";

    /// <summary>只有一条带距离的参照，算不出间隔。</summary>
    public const string SingleHit = "single_hit";

    /// <summary>间隔未达拒答阈值：前几名挤在一起，没有赢家。</summary>
    public const string NoWinner = "no_winner";

    /// <summary>有赢家但第一名不够近。</summary>
    public const string TooFar = "too_far";

    /// <summary>两个阈值没有同时配置，什么都判不了。</summary>
    public const string ThresholdUnset = "threshold_unset";
}

/// <summary>
/// 一次判定的结果。
/// </summary>
/// <param name="Verdict">四值结论，取值见 <see cref="SufficiencyVerdict"/>。</param>
/// <param name="TopDistance">集合里最小的那个距离（不是「排第一那条」的距离）。没有带距离的命中时为 null。</param>
/// <param name="Margin">第二名到第五名的平均距离减去第一名距离；带距离的命中不足两条时为 null。</param>
/// <param name="Reason">原因码，取值见 <see cref="SufficiencyReason"/>；「已回答」为 null。</param>
public sealed record SufficiencyResult(string Verdict, double? TopDistance, double? Margin, string? Reason);

/// <summary>
/// 判定的两个旋钮。都可空：未测出来时留空，含义是「没有能力判断」，不是「判定通过」。
/// </summary>
/// <param name="RefuseMargin">间隔低于它判不足。</param>
/// <param name="SufficientDistance">第一名距离低于它才算够近。与 <paramref name="RefuseMargin"/> 必须同时在场的，才判得出「已回答」。</param>
public sealed record SufficiencyOptions(double? RefuseMargin, double? SufficientDistance)
{
    /// <summary>两个都没配——当前配置的默认状态。</summary>
    public static SufficiencyOptions Unset { get; } = new(null, null);

    /// <summary>两个都配了，才具备判「已回答」的能力。</summary>
    public bool IsComplete => RefuseMargin is not null && SufficientDistance is not null;
}

/// <summary>
/// 「够不够」的判定。纯函数：进 DI 会引入状态，进数据库会引入时间，两者都让它无法复算。
/// </summary>
/// <remarks>
/// 规格是文档里的「判定用例表」，代码只是它的翻译。规则改动时先改表，再改这里。
/// </remarks>
public static class SufficiencyGate
{
    /// <summary>参与间隔计算的命中条数上限：第六名往后进不了上下文，不该影响判定。</summary>
    public const int MarginWindow = 5;

    /// <summary>
    /// 按用例表判定本次检索够不够。
    /// </summary>
    /// <param name="hits">本次检索的命中；顺序不可信，内部自己排。</param>
    /// <param name="options">两个可空阈值。</param>
    public static SufficiencyResult Judge(IReadOnlyList<RetrievalHit> hits, SufficiencyOptions options)
    {
        if (hits is null || hits.Count == 0)
            return new SufficiencyResult(SufficiencyVerdict.Insufficient, null, null, SufficiencyReason.NoCandidates);

        // 只取带距离的、有限的、最小的那几条。空距离与非有限值一律视同"这条没有距离"：
        // 当零会把间隔算小（误判不足），按总条数除会把间隔算大（误判已回答），两种错都长得像正常结果。
        var distances = hits
            .Select(hit => hit.VectorScore)
            .Where(score => score is { } value && double.IsFinite(value))
            .Select(score => score!.Value)
            .Order()
            .Take(MarginWindow)
            .ToList();

        if (distances.Count == 0)
            return new SufficiencyResult(SufficiencyVerdict.Undetermined, null, null, SufficiencyReason.NoVector);

        // 第一名取"集合里最小的距离"，不看它排第几：排序是融合算的，排第一的块可能来自全文路（没有距离）。
        var top = distances[0];

        if (distances.Count == 1)
            return new SufficiencyResult(SufficiencyVerdict.Weak, top, null, SufficiencyReason.SingleHit);

        var margin = distances.Skip(1).Average() - top;

        // 比较一律用严格小于：等于阈值不拒、等于充分距离不算够近。改成 <= 会让边界行为漂移，而两边都能自证合理。
        if (options.RefuseMargin is { } refuseMargin && margin < refuseMargin)
            return new SufficiencyResult(SufficiencyVerdict.Insufficient, top, margin, SufficiencyReason.NoWinner);

        // 要求两个阈值同时在场：只配充分距离时，"挤在一起但恰好都很近"会被判成已回答，而那正是没有赢家的形状。
        if (options.IsComplete && top < options.SufficientDistance!.Value)
            return new SufficiencyResult(SufficiencyVerdict.Answered, top, margin, null);

        if (!options.IsComplete)
            return new SufficiencyResult(SufficiencyVerdict.Undetermined, top, margin, SufficiencyReason.ThresholdUnset);

        return new SufficiencyResult(SufficiencyVerdict.Weak, top, margin, SufficiencyReason.TooFar);
    }
}

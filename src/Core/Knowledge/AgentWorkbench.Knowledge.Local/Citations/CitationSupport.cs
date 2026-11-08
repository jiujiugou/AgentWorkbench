namespace AgentWorkbench.Knowledge.Local.Citations;

/// <summary>整段答案的支撑结论：这一整段下面有没有资料撑着。</summary>
public enum SupportVerdict
{
    /// <summary>无出处句比例低于判定线——多数句子挂上了出处。</summary>
    Supported,

    /// <summary>无出处句比例达到判定线——这段话基本没有资料在下面撑着。</summary>
    Unsupported,

    /// <summary>判定线未配置，或答案为空——只报比例，不下结论。</summary>
    Undetermined
}

/// <summary>
/// 第二层「承认」的判定：把已有的句级报告翻译成一句结论。
/// </summary>
/// <remarks>
/// <para>
/// 这一层不拒答。它管的是那一档"同主题但手册里没写这句话"的问题——拒了会误伤真实问题，
/// 放过去又和"有资料支撑"长得一模一样。唯一诚实且不越界的做法，是让"没有出处"这件事显式到
/// 用户不可能看漏：不是摘要行里一行小字，是整段被标出来。
/// </para>
/// <para>
/// <b>为什么用无出处句比例，而不是覆盖率或引用核实率</b>：覆盖率是"这句和所引那块像不像"，
/// 挂了出处但引错块时才低；比例是"这段有多少句根本没挂出处"，正对"凭常识补"这种失败方式。
/// 两者查的不是一件事，这里要的是后者。
/// </para>
/// <para>
/// <b>阈值可空，且 0 是合法值</b>——与 <c>RefuseMargin</c> 相反。拒答阈值配 0 等于"永不拒"，
/// 看起来却像配过了，所以那里禁止；这里的 0 有明确含义："只要有一句没出处就标出来"，
/// 是个真能被选中的策略，因此不做特殊处理。
/// </para>
/// </remarks>
public static class CitationSupport
{
    /// <summary>
    /// 判一段答案有没有资料支撑。
    /// </summary>
    /// <param name="report">引用体检报告。</param>
    /// <param name="threshold">无出处句比例的判定线（0 到 1）；<c>null</c> 表示未配置，只报比例不下结论。</param>
    public static SupportVerdict Judge(CitationReport report, double? threshold)
    {
        // 空答案没有"支撑"可言，也不该被标成"没有支撑"——那是把没说话当成说错话。
        if (report.SentenceCount <= 0)
            return SupportVerdict.Undetermined;

        if (threshold is not { } line)
            return SupportVerdict.Undetermined;

        // 先过这一关再接比例：一句都没缺的时候，谈不上"没有支撑"，无论阈值多低。
        // 少了这一句，阈值 0 会把"全部有出处"也标成无支撑——0.0 >= 0 成立，
        // 于是这条判定在最该正常的输入上给出最坏的结果，而且没人会怀疑是它错了。
        if (report.UncitedSentenceCount <= 0)
            return SupportVerdict.Supported;

        return UncitedRatio(report) >= line ? SupportVerdict.Unsupported : SupportVerdict.Supported;
    }

    /// <summary>无出处句占全部句子的比例；没有句子时返回 0，不返回 NaN。</summary>
    public static double UncitedRatio(CitationReport report)
        => report.SentenceCount <= 0 ? 0 : (double)report.UncitedSentenceCount / report.SentenceCount;
}

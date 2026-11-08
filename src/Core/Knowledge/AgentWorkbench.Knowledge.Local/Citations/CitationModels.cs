namespace AgentWorkbench.Knowledge.Local.Citations;

/// <summary>一条引用的校验结论。</summary>
public enum CiteCheck
{
    /// <summary>编号落在本次证据范围内，且内容覆盖率达标（或阈值未配置，只记录不判定）。</summary>
    Ok,

    /// <summary>编号不在本次证据范围内——它指向一个不存在的东西。</summary>
    OutOfRange,

    /// <summary>编号存在，但该句内容在所指那一块里找不到对应——号码对、内容对不上（误挂）。</summary>
    Unsupported
}

/// <summary>
/// 答案里的一处出处标注（一句可以挂多处）。
/// </summary>
/// <param name="SentenceIndex">所属句子序号（从 0 起）。</param>
/// <param name="EvidenceNo">模型写的编号（从 1 起，对应检索结果里的 <c>no</c>）。</param>
/// <param name="FileName">该编号对应的文件名；越界时为空串。</param>
/// <param name="Seq">该编号对应的片段序号；越界时为 0。</param>
/// <param name="ChunkId">该编号对应的块编号；越界时为 0。</param>
/// <param name="Check">校验结论。</param>
/// <param name="Coverage">该句与所指块的字符覆盖率；越界时为 null。</param>
public sealed record Citation(
    int SentenceIndex,
    int EvidenceNo,
    string FileName,
    int Seq,
    long ChunkId,
    CiteCheck Check,
    double? Coverage);

/// <summary>
/// 一次作答的引用体检报告：句级统计 + 逐条明细 + 剥离越界标记后的正文。
/// </summary>
/// <param name="SentenceCount">答案总句数。</param>
/// <param name="CitedSentenceCount">至少挂了一处有效出处（Ok 或 Unsupported）的句子数。</param>
/// <param name="UncitedSentenceCount">一处出处都没挂的句子数。</param>
/// <param name="OutOfRangeCount">越界标注条数。</param>
/// <param name="UnsupportedCount">指向错块的标注条数。</param>
/// <param name="CleanedAnswer">删掉越界标注后的正文（越界编号指不存在的东西，留着就是假担保）。</param>
/// <param name="Citations">逐条明细，按出现顺序。</param>
public sealed record CitationReport(
    int SentenceCount,
    int CitedSentenceCount,
    int UncitedSentenceCount,
    int OutOfRangeCount,
    int UnsupportedCount,
    string CleanedAnswer,
    IReadOnlyList<Citation> Citations)
{
    /// <summary>没有任何出处的空报告（非知识问答、或答案为空时使用）。</summary>
    public static CitationReport Empty(string answer = "") =>
        new(0, 0, 0, 0, 0, answer, []);

    /// <summary>挂在正文上的标注总数。</summary>
    public int CitationCount => Citations.Count;
}

/// <summary>解析中间产物：切好的句子 + 尚未校验的原始标注。</summary>
/// <param name="Sentences">句子原文（含句末标点）。</param>
/// <param name="Raw">原始标注：所属句序号与编号，按出现顺序。</param>
public sealed record ParsedAnswer(
    IReadOnlyList<string> Sentences,
    IReadOnlyList<(int SentenceIndex, int EvidenceNo)> Raw);

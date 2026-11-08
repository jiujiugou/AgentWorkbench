namespace AgentWorkbench.Knowledge.Local;

/// <summary>拒答时给用户看的那句话。</summary>
/// <remarks>
/// 固定文案的意义在于"它不可能被模型改写"：一旦允许模型自己组织"资料里没有"的说法，
/// 同一个判定会在不同轮里长得不一样，既不可测，也没法断言。
/// 这段话由编排层写入答复，模型那条路此时已经断了——它一个字都产不出来。
/// </remarks>
public static class KnowledgeRefusal
{
    /// <summary>判据里那句"固定拒答文案"。</summary>
    public const string Text = "资料不足以回答该问题。知识库里没有找到能支撑回答的内容。";
}

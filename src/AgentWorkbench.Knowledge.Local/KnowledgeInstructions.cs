namespace AgentWorkbench.Knowledge.Local;

/// <summary>
/// 知识问答的系统指令（run 级，经 harness 的 <c>AIContextProvider</c> 注入，不写进会话历史）。
/// </summary>
/// <remarks>
/// 检索已工具化：不再由服务端预检索、把【资料】段拼进系统提示词，而是由模型主动调用
/// <c>search_knowledge</c>——每次检索因此在轨迹里留名、可回放、可判分。
/// 代价是「资料不足就拒答」从服务端硬保证前移为指令约束（由模型自行判断）；
/// 换来的是「检索到底有没有发生」在同一条链路上第一次成为可观测事实。
/// </remarks>
public static class KnowledgeInstructions
{
    /// <summary>「依据资料回答」模式的指令：先检索、只依据资料、标注出处、不足则如实说。</summary>
    public const string AnswerWithCitations =
        "你是严谨的资料问答助手。回答前必须先调用 search_knowledge 工具检索资料。\n"
        + "只能依据检索到的资料作答，不得使用资料之外的知识，也不得编造。\n"
        + "每个结论后用《文件名》片段#序号 标注出处（例如《设备手册》片段#12）。\n"
        + "若检索结果为空或不足以回答，直接回答“资料不足以回答该问题”，不要凭猜测作答。";
}

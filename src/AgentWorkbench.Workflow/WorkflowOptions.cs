namespace AgentWorkbench.Workflow;

/// <summary>
/// 工作流种子配置（配置节 <c>Workflow</c>）。
/// </summary>
/// <remarks>
/// 提示词是<b>一等公民</b>：默认提示词不再硬编码在种子代码里，而是落在配置
/// （appsettings / 环境变量）中，改提示词不必改代码重新编译。
/// 运行期改提示词仍走「新增提示词版本 + 激活」（前端提示词面板 / <c>IPromptStore</c>），
/// 本节只负责「库里一条都没有时，播什么」。
/// </remarks>
public sealed class WorkflowOptions
{
    public const string SectionName = "Workflow";

    /// <summary>默认工作流定义 key（不变，切换场景只换提示词版本）。</summary>
    public string DefaultWorkflowKey { get; set; } = "document_analysis";

    /// <summary>agent 步骤使用的提示词 key（不变，切换场景只换版本）。</summary>
    public string AnswerPromptKey { get; set; } = "workflow.answer";

    /// <summary>默认提示词版本号。</summary>
    public string DefaultPromptVersion { get; set; } = "base-0.1";

    /// <summary>
    /// 默认提示词内容。留空表示不播种提示词（由运营在前端自行新增版本）。
    /// </summary>
    /// <remarks>
    /// 「依据不足就直说」是硬约束：编造依据的代价远高于拒答。
    /// 默认值与场景无关（只说「依据资料作答 + 强制引用 + 不足则拒答」），
    /// 具体领域人格由运营按版本切换 —— 不把垂直场景写死在代码里。
    /// </remarks>
    public string DefaultPromptContent { get; set; } =
        "你是文档分析助手。只依据给定的【文档上下文】与【检索到的依据】作答，"
      + "输出结构化的结论与建议。引用依据时用 [编号] 标注，编号必须与【检索到的依据】中的编号一致，"
      + "不得引用不存在的编号，不得编造依据中不存在的参数、条款或事实。"
      + "依据不足以支撑结论时，明确标注「依据不足，需人工确认」并列出仍需确认的项，"
      + "不得用通用经验冒充依据。";

    /// <summary>默认工作流显示名。</summary>
    public string DefaultWorkflowName { get; set; } = "文档分析工作流";
}

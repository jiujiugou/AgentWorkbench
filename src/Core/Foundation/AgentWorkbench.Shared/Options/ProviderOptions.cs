namespace AgentWorkbench.Shared.Options;

/// <summary>
/// 大模型供应商配置（任意 OpenAI 兼容端点）。
/// 配置来源优先级：环境变量 &gt; appsettings（后续可扩展为数据库 settings 表优先）。
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Provider";

    /// <summary>OpenAI 兼容端点，如 https://api.deepseek.com/v1 </summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>API Key（私有化场景建议用环境变量注入，不落库）</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>对话模型，如 deepseek-chat / qwen-plus</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>Embedding 模型（用于 RAG，可选）</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>采样温度</summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>
    /// 是否允许向模型发送函数工具（function calling）。
    /// 部分 OpenAI 兼容端点/推理模型（如 SiliconFlow 的 GLM-Z1）不支持 <c>tools</c>，置 false 可避免 400。
    /// </summary>
    public bool SupportsTools { get; set; } = true;

    /// <summary>系统提示词</summary>
    public string SystemPrompt { get; set; } =
        "你是 AgentWorkbench 的通用 AI 助手，回答准确、简洁，必要时使用工具。";

    /// <summary>是否已具备可用配置</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model);
}

namespace AgentWorkbench.ToolExecution;

/// <summary>工具执行配置（配置节 <c>Harness:ToolExecution</c>）。</summary>
public sealed class ToolExecutionOptions
{
    public const string SectionName = "Harness:ToolExecution";

    /// <summary>轨迹里保存的工具结果最大字符数（超出截断，避免轨迹表被大结果撑爆）。</summary>
    public int MaxResultChars { get; set; } = 400;
}

namespace AgentWorkbench.ToolExecution;

/// <summary>工具执行配置（配置节 <c>Harness:ToolExecution</c>）。</summary>
public sealed class ToolExecutionOptions
{
    public const string SectionName = "Harness:ToolExecution";

    /// <summary>轨迹里保存的工具结果最大字符数（超出截断，避免轨迹表被大结果撑爆）。</summary>
    public int MaxResultChars { get; set; } = 400;

    /// <summary>
    /// 审批等待上限（秒）：<c>Ask</c> 的工具在这段时间内拿不到批准即按拒绝处理（fail-closed）。
    /// 管道侧的超时兜底——审批执行者自己还没超时也不会让整轮无限挂住。&lt;=0 表示不设上限（仅测试用）。
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 120;
}

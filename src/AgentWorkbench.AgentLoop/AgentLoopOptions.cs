namespace AgentWorkbench.AgentLoop;

/// <summary>
/// 执行控制配置（配置节 <c>Harness:Loop</c>）：整个任务的迭代预算与总耗时上限。
/// </summary>
public sealed class AgentLoopOptions
{
    public const string SectionName = "Harness:Loop";

    /// <summary>单次 run 的最大迭代步数（= 工具环上限）。</summary>
    public int MaxIterations { get; set; } = 6;

    /// <summary>单次 run 的总超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

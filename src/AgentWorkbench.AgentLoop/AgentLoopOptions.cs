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

    /// <summary>
    /// 连续重复调用「同一工具、同一参数」的次数上限；达到即判定「卡在同一个调用上」并终止。
    /// <c>0</c> = 关闭检测。
    /// </summary>
    /// <remarks>
    /// 默认 3：同样的调用连着来三次，几乎只可能是循环，而不是模型在有序地推进任务。
    /// 只统计「连续」重复——中间夹了别的调用就重新计数，避免误伤正常的多轮调用。
    /// </remarks>
    public int MaxRepeatedToolCalls { get; set; } = 3;

    /// <summary>单次 run 的 token 预算（输入 + 输出）；超出即终止。<c>0</c> = 不限制。</summary>
    /// <remarks>
    /// 墙钟超时管不住「钱」：一次 run 可以在超时窗口内把 token 烧光。默认不限制，
    /// 需要限额的宿主自己在 <c>Harness:Loop:MaxTokensPerRun</c> 里给数字。
    /// </remarks>
    public int MaxTokensPerRun { get; set; }
}

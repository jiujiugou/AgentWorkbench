using AgentWorkbench.ToolExecution;

namespace AgentWorkbench.AgentHarness;

/// <summary>流式 run 的事件类型。一次 run 的事件序列以 <see cref="Completed"/> 或 <see cref="Failed"/> 收尾（且只有一个）。</summary>
public enum HarnessStreamEventKind
{
    /// <summary>模型文本增量（<see cref="HarnessStreamEvent.Text"/> 非空）。</summary>
    TextDelta,

    /// <summary>一次工具调用已完成——成功 / 失败 / 被守卫终止都算完成（<see cref="HarnessStreamEvent.Tool"/> 非空）。
    /// 结果未截断，供调用方做实时呈现（如知识库来源面板）。</summary>
    ToolCompleted,

    /// <summary>run 正常收尾（含跑满迭代等提前终止）：轨迹已落库，<see cref="HarnessStreamEvent.Result"/> 非空。</summary>
    Completed,

    /// <summary>run 失败 / 取消 / 超时：轨迹已落库，<see cref="HarnessStreamEvent.Error"/> 为面向用户的错误描述。</summary>
    Failed
}

/// <summary>
/// 一次流式 run 的事件。与 <see cref="IAgentHarness.RunAsync"/> 同源：同一 loop、同一 guard、同一轨迹落库，
/// 差别只是产出形态（实时事件流 vs 最终结果）。
/// </summary>
/// <param name="Kind">事件类型。</param>
/// <param name="Text">文本增量；仅 <see cref="HarnessStreamEventKind.TextDelta"/> 非空。</param>
/// <param name="Tool">工具调用轨迹（结果未截断）；仅 <see cref="HarnessStreamEventKind.ToolCompleted"/> 非空。</param>
/// <param name="Result">最终结果；仅 <see cref="HarnessStreamEventKind.Completed"/> 非空。</param>
/// <param name="Error">面向用户的错误描述；仅 <see cref="HarnessStreamEventKind.Failed"/> 非空。</param>
public sealed record HarnessStreamEvent(
    HarnessStreamEventKind Kind,
    string? Text = null,
    ToolCallTrace? Tool = null,
    HarnessRunResult? Result = null,
    string? Error = null);

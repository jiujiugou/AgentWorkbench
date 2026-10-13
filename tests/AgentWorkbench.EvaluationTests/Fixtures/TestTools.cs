using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.EvaluationTests.Fixtures;

/// <summary>
/// 测试专用故障注入工具（探针，不是产品能力）：让两条生产里从未被稳定触发过的路径可复现——
/// 工具反复抛异常（失败分类），以及慢工具被取消（取消是否穿透进工具内部）。
/// 额外提供 <see cref="HangGate"/>：hang 模式可挂到外部闸门上，让并发负控能确定性排序
/// （先让一个 run 停在工具执行里，再放另一个 run 烧用量，最后放行——无竞态）。
/// </summary>
internal sealed class FaultInjectorTool : IAgentToolProvider
{
    public const string ToolName = "fault_injector";

    private int _calls;
    private int _canceled;

    /// <summary>hang 模式的外部闸门：非空时等待它而不是按 delayMs 定时。</summary>
    public TaskCompletionSource? HangGate { get; set; }

    /// <summary>工具被实际调用的次数（并发安全）。</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>等待期间取消是否真的发生在工具内部（外层超时不会置真，只有穿透进来的取消才会）。</summary>
    public bool ObservedCancellation => Volatile.Read(ref _canceled) != 0;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            (string mode, int failTimes, int delayMs, CancellationToken cancellationToken)
                => InvokeAsync(mode, failTimes, delayMs, cancellationToken),
            ToolName,
            "故障注入工具（评测夹具）：mode=throw 连续失败 failTimes 次；mode=hang 延迟 delayMs 毫秒（可被外部闸门挂起）且可被取消。")
    ];

    private async Task<string> InvokeAsync(string mode, int failTimes, int delayMs, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);

        if (string.Equals(mode, "hang", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (HangGate is not null)
                {
                    await HangGate.Task;   // 外部闸门：等待测试放行（本身不可取消，由调用方令牌在外层兜底）
                }
                else
                {
                    await Task.Delay(delayMs > 0 ? delayMs : Timeout.Infinite, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 只记录、不改语义：取消照旧抛回上层。
                Interlocked.Exchange(ref _canceled, 1);
                throw;
            }

            return "ok";
        }

        if (call <= failTimes)
            throw new InvalidOperationException($"故障注入：第 {call} 次调用失败（failTimes={failTimes}）");

        return "ok";
    }
}

/// <summary>固定工具目录：由测试直接决定「当前允许哪些工具」。</summary>
internal sealed class FakeToolCatalog(IReadOnlyList<AITool> tools) : IToolCatalog
{
    public FakeToolCatalog() : this([])
    {
    }

    public IReadOnlyList<AITool> GetTools() => tools;

    public IReadOnlyList<string> GetToolNames() => tools.Select(tool => tool.Name).ToList();
}

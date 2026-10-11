using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.UnitTests.Tools;

/// <summary>
/// 测试专用故障注入工具（探针）。它是测试夹具、不是产品能力，所以只放在测试项目里，
/// 不进 ToolExecution/Builtin —— 生产工具目录里不应出现「故意炸」的工具。
///
/// 用途：让两条在生产里从未真正被触发过的路径可以被稳定复现——
/// 一是工具反复抛异常（失败分类 / 连续失败上限），二是慢工具被取消（取消是否真穿透进工具内部）。
///
/// 参数：mode=throw 时前 failTimes 次调用抛 InvalidOperationException，之后返回 ok；
///       mode=hang  时按 delayMs 毫秒等待，等待可被 CancellationToken 打断。
/// 观测点：Calls 记录真实调用次数，ObservedCancellation 记录取消是否真的发生在工具内部。
/// </summary>
internal sealed class FaultInjectorTool : IAgentToolProvider
{
    public const string ToolName = "fault_injector";

    private int _calls;
    private int _canceled;

    /// <summary>工具被实际调用的次数（并发安全）。</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>等待期间是否在工具内部真的收到了取消。外层超时不会把它置真，只有穿透进来的取消才会。</summary>
    public bool ObservedCancellation => Volatile.Read(ref _canceled) != 0;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            (string mode, int failTimes, int delayMs, CancellationToken cancellationToken)
                => InvokeAsync(mode, failTimes, delayMs, cancellationToken),
            ToolName,
            "故障注入工具（测试夹具）：mode=throw 连续失败 failTimes 次；mode=hang 延迟 delayMs 毫秒且可被取消。")
    ];

    private async Task<string> InvokeAsync(string mode, int failTimes, int delayMs, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);

        if (string.Equals(mode, "hang", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // 用可取消的等待：取消必须真的打断它，而不是等它跑完再由外层判超时。
                await Task.Delay(delayMs > 0 ? delayMs : Timeout.Infinite, cancellationToken);
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

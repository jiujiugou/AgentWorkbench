// 功能未实现，本组预期红，红是正确信号。
// F2.10「幂等键：只透传 / 记录，不生成不解析」当前不存在：ToolCallTrace 只有
// Iteration/Name/Arguments/Result/Error 五个字段，Runtime.Tools 程序集里没有任何幂等键类型。
// 本组不引用任何不存在的类型：用现有公开 API 断言「业务键作为普通参数确实透传」（绿对照），
// 再用反射断言「幂等键是一等公民」能力存在（红目标）。
// 每条用例整体预期红；把幂等键做成一等公民字段/类型后，本组应转绿。

using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

public class ToolIdempotencyKeyTests
{
    // [F2.10] 传入带业务含义的键，应被「专门记录」为一等公民，而不只是混在 Arguments JSON 里。
    // 绿对照（透传基础存在）：request_id=order-42 确实进了轨迹 Arguments。
    // 红目标：ToolCallTrace 上应有专门的幂等键属性（IdempotencyKey / RequestId 之类）；当前一个都没有 → 红。
    // 负控（实现后转绿的判据）：给 ToolCallTrace 加一个只透传、不生成、不解析的幂等键字段并回填，
    //       本条转绿；当前恒红。
    [Fact]
    public async Task 幂等键应作为一等公民被专门记录_普通参数透传作对照()
    {
        var tool = CountingTool.NoArg("submit", () => "OK");
        var arguments = new Dictionary<string, object?> { ["request_id"] = "order-42" };
        var inner = new ScriptedChatClient().ToolCall("submit", arguments).Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Contains("order-42", result.Run.Traces[0].Arguments!); // 绿对照：业务键确实透传

        var keyProperties = typeof(ToolCallTrace).GetProperties()
            .Where(p => p.Name.Contains("Idempoten", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("RequestId", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(keyProperties); // 红目标：当前 ToolCallTrace 无幂等键一等公民字段
    }

    // [F2.10] 反射断言 Runtime.Tools 程序集里存在幂等键相关类型。
    // 绿对照：程序集里确实能枚举出类型；红目标：名含 Idempoten 的类型当前为 0 → Assert.NotEmpty 红。
    // 负控：新增幂等键值对象 / 记录类型后本条转绿。
    [Fact]
    public void 幂等键相关类型应存在()
    {
        var allTypes = typeof(ToolCallTrace).Assembly.GetTypes();
        Assert.NotEmpty(allTypes); // 绿对照

        var idempotencyTypes = allTypes
            .Where(t => t.Name.Contains("Idempoten", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(idempotencyTypes); // 红目标：当前程序集里没有任何幂等键类型
    }
}

// 功能未实现，本组预期红，红是正确信号。
// F2.8「调用前按 schema 校验」当前不存在（规格 §2.2 / §2.3：Validate / Schema 零命中，畸形参数直达执行体）。
// 本组不引用任何不存在的类型：要么用现有公开 API 断言期望行为，要么用反射断言能力存在。
// 每条用例内部先给「该放行的」绿对照，再给「该拒的」红目标；整条用例的目标断言预期红。
// 把 schema 校验补进 ToolExecutionPipeline（InvokeAsync 之前校验并回填拒绝码）后，本组应转绿。

using System.Reflection;
using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

public class ToolParameterValidationTests
{
    // [F2.8] 缺必填 / 类型错应在调用前被 schema 校验拦下，工具 0 次执行、轨迹给出明确的校验拒绝码。
    // 负控（实现后转绿的判据）：在 ToolExecutionPipeline.cs:177 的 InvokeAsync 之前加一层
    //       JsonSchema 校验，畸形参数直接回填 "argument_validation_failed" 而不进执行体；
    //       当前无此层，bad 轨迹的 Error 是异常消息或 null，永远不等于该码 → 本条红。
    [Fact]
    public async Task 畸形参数应在执行前被拒_合法参数放行作对照()
    {
        // 绿对照（该放行的）：参数合法 → 执行 1 次、轨迹无 error
        var okTool = CountingTool.OneArg("calc", input => "结果:" + input);
        var okInner = new ScriptedChatClient()
            .ToolCall("calc", new Dictionary<string, object?> { ["input"] = "1+2" })
            .Reply("完成");
        var ok = await PipelineDriver.DriveAsync(
            okInner, okTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, okTool.Executions);
        Assert.Null(ok.Run.Traces[0].Error);

        // 红目标（该拒的）：input 传 int（类型错），期望被 schema 校验拦在执行前
        var badTool = CountingTool.OneArg("calc", input => "不该被执行");
        var badInner = new ScriptedChatClient()
            .ToolCall("calc", new Dictionary<string, object?> { ["input"] = 12345 })
            .Reply("换路");
        var bad = await PipelineDriver.DriveAsync(
            badInner, badTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal("argument_validation_failed", bad.Run.Traces[0].Error);
        Assert.Equal(0, badTool.Executions);
    }

    // [F2.8] 反射断言 ToolExecutionPipeline 上存在 schema / Validate 校验能力。
    // 绿对照：反射能看到声明成员中的 Create（证明扫描有效）。
    // 红目标：声明成员里应有 Validate/Schema/JsonSchema 相关成员；当前一个都没有 → Assert.NotEmpty 红。
    // 负控：在 ToolExecutionPipeline 里真正实现 Validate(arguments, schema) 后本条转绿。
    [Fact]
    public void schema_校验能力应存在()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.DeclaredOnly;
        var declared = typeof(ToolExecutionPipeline).GetMembers(flags).ToList();

        Assert.Contains(declared, m => m.Name == "Create"); // 绿对照

        var validationMembers = declared
            .Where(m => m.Name.Contains("Validate", StringComparison.OrdinalIgnoreCase)
                     || m.Name.Contains("Schema", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(validationMembers); // 红目标
    }
}

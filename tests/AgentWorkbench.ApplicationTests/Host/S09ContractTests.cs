using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;
using AgentWorkbench.TestKit;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S09（结果回传模型）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>本组与模块级单测不关联</b>：这一格归属写的是 Core（<c>Runtime.Loop</c> + <c>Runtime.Tools</c>），
/// 仓里也确实另有一整套守卫/超时/归类码的模块级单测（CoreTests 与 ApplicationTests/Runtime 下）。
/// <b>那两套东西和本组互不迁移、互不复用、互不计数</b>：S 系列是「一次 run 的流程验收」，
/// 与其余 Sxx 同处 Host 项目、走门禁默认项目；模块级单测验的是单个类的行为。
/// 因此 <c>scripts/stage_gate.py</c> 不需要为 S09 改动，也不该把 S09 指到别的项目。
///
/// <b>边界</b>：起于工具环跑起来，止于「最终文本答复或一个明确终态」交给 S10。
/// 四栏覆盖：①反复调同一工具 → 01、06；②迭代上限没收口 → 02；③工具失败被当成功 → 04；
/// 「交给下一格什么」→ 05；「不许留下什么」→ 01、02、03（三种强制收口都带归类码）。
/// <b>不归本格</b>：模型说了什么（S04）；落库（S10）。
///
/// <b>登记在案的空白（不写占位测试、不写恒真的绿）</b>：
/// <list type="number">
/// <item>本组验的是「守卫在环内 / 收尾给出了归类码，且工具环确实停下」。
/// <c>AgentHarness</c> 如何把 <c>InspectCompletion()</c> 的停因接到最终 run 状态与轨迹，
/// 是另一段装配，本组一条都没验（与 S03 登记的「Harness 看不到循环中间态」是同一处缺口）。</item>
/// <item>token 预算用的是本地递增用量替身，不是真实模型计量（<c>ModelUsageMeter</c>）；
/// 「读数确实来自模型」不在这里验。</item>
/// <item>三种终止之外的墙钟超时（<c>AgentLoopService</c>）由模块级单测覆盖，本组不重复。</item>
/// <item><b>实测发现</b>：工具抛异常时，MEA 回填给模型的 <c>FunctionResultContent.Result</c>
/// <b>不含原始异常文本</b>（04 用正常工具作绿对照证明提取器有效，仍读不到 <c>boom</c>）。
/// 因此文档怕什么③「工具失败被模型当成成功」本组只验到「系统没把失败记成成功 +
/// 异常仍被回填成一条结果消息」这一半；「模型确实读懂了失败」没有证据，是已知缺口，未修。</item>
/// </list>
///
/// <b>层 × 方向</b>：01–06 全在 Core（<see cref="AgentRunGuard"/> + <see cref="ToolExecutionPipeline"/>）；
/// 测试替身只有脚本化模型、可计数工具与审批策略，生产侧的执行控制守卫是真的，不是 Mock。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S09ContractTests
{
    // [S09-01] 「模型反复调同一个工具」→ 强制收口，且状态带归类码与可量化依据。
    // 第 3 次同参调用在发出前就被挡下，所以执行体只跑 2 次、轨迹 3 笔、最后一笔带 error。
    // 负控：把 AgentRunGuard.DetectLoop 的达标判断去掉（恒返回 null）→ 本条红。
    [Fact]
    public async Task S09_01_重复调用到上限时强制收口且带归类码()
    {
        var tool = CountingTool.NoArg("t", () => "ok");
        var model = new ScriptedChatClient().ToolCall("t");
        model.RepeatLast = true;
        var guard = Guard(maxIterations: 6, maxRepeated: 3);

        var (run, _, thrown) = await DriveAsync(model, guard, 6, tool.Tool);

        Assert.Null(thrown);
        Assert.NotNull(run.Stop);
        Assert.Equal(AgentRunStatus.LoopDetected, run.Stop!.Status);
        Assert.Equal(AgentRunCode.LoopDetected, run.Stop.Code);
        Assert.Equal("repeated_tool_calls", run.Stop.Detail!.Kind);
        Assert.Equal(3, run.Stop.Detail.Observed);
        Assert.Equal(3, run.Stop.Detail.Limit);
        Assert.Equal("t", run.Stop.Detail.Subject);
        Assert.Equal(2, tool.Executions);
        Assert.Equal(3, run.Traces.Count);
        Assert.Contains(AgentRunCode.LoopDetected, run.Traces[^1].Error);
    }

    // [S09-02] 「迭代超限没收口」：跑满预算时收尾分类必须给 iteration_limit，
    // 而不是把被逼出来的文本当成「模型自己答完了」。
    // 负控：把 InspectCompletion 的停因改成 Succeeded/ok → 本条红。
    [Fact]
    public async Task S09_02_迭代预算跑满时收尾带iteration_limit()
    {
        var tool = CountingTool.NoArg("t", () => "ok");
        var model = new ScriptedChatClient().ToolCall("t").Reply("被逼收口的答复");
        var guard = Guard(maxIterations: 1, maxRepeated: 0);   // 关掉重复检测，只验迭代

        var (run, text, _) = await DriveAsync(model, guard, 1, tool.Tool);

        Assert.Equal("被逼收口的答复", text);
        Assert.Equal(1, tool.Executions);
        Assert.Null(run.Stop);                                  // 环内没提前终止
        var stop = guard.InspectCompletion();
        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.IterationLimit, stop!.Status);
        Assert.Equal(AgentRunCode.IterationLimit, stop.Code);
        Assert.Equal("iteration_budget", stop.Detail!.Kind);
        Assert.Equal(stop.Detail.Limit, stop.Detail.Observed);
    }

    // [S09-03] 「预算耗尽 → 强制收口，状态带归类码」：第一次调用在发出前就被终止，
    // 执行体一次都不该跑。
    // 负控：把 DetectBudgetOverage 的达标判断去掉 → 本条红。
    [Fact]
    public async Task S09_03_预算耗尽时强制收口且带归类码()
    {
        var tool = CountingTool.NoArg("t", () => "ok");
        var model = new ScriptedChatClient().ToolCall("t").Reply("答复");
        var guard = Guard(maxTokens: 50, usage: new RisingUsage(60));

        var (run, _, _) = await DriveAsync(model, guard, 6, tool.Tool);

        Assert.NotNull(run.Stop);
        Assert.Equal(AgentRunStatus.BudgetExceeded, run.Stop!.Status);
        Assert.Equal(AgentRunCode.BudgetExceeded, run.Stop.Code);
        Assert.Equal("token_budget", run.Stop.Detail!.Kind);
        Assert.Equal(0, tool.Executions);
        Assert.Contains(AgentRunCode.BudgetExceeded, run.Traces[^1].Error);
    }

    // [S09-04] 「工具失败被模型当成成功」能证实的那一半：轨迹里留下 Error 而不是 Result、
    // 异常被记进 ToolFailures，且模型第二轮确实收到一条结果消息（不是被静默吞掉）。
    // 先用正常工具作绿对照，证明 ResultPayload 提取器有效、下面的断言不是恒真空串。
    // 负控：把 ToolExecutionPipeline catch 里的 error 写成 null → 本条红。
    // 回填内容里读不到原始异常文本这一半是已知缺口，见头部登记 4。
    [Fact]
    public async Task S09_04_工具失败时轨迹标失败且异常仍回填结果消息()
    {
        var okTool = CountingTool.NoArg("t", () => "正常结果");
        var okModel = new ScriptedChatClient().ToolCall("t").Reply("完成");
        await DriveAsync(okModel, Guard(), 6, okTool.Tool);
        Assert.Contains("正常结果", ResultPayload(okModel.Requests[1]));

        var tool = CountingTool.NoArg("t", () => throw new InvalidOperationException("boom"));
        var model = new ScriptedChatClient().ToolCall("t").Reply("我看到了错误");
        var guard = Guard();

        var (run, text, _) = await DriveAsync(model, guard, 6, tool.Tool);

        Assert.Equal("我看到了错误", text);
        Assert.Equal(2, model.CallCount);
        Assert.Single(run.Traces);
        Assert.Null(run.Traces[0].Result);
        Assert.Contains("boom", run.Traces[0].Error!);
        Assert.Single(run.ToolFailures);
        Assert.True(
            model.Requests[1].SelectMany(m => m.Contents).Any(c => c is FunctionResultContent),
            "工具异常没有被转成一条结果消息回到模型，回填通道不成立");
        Assert.DoesNotContain("正常结果", ResultPayload(model.Requests[1]));
    }

    // [S09-05] 「交给下一格什么」的正常路径：模型的最终文本答复，没有停因、没有被判成截断。
    // 负控：让 InspectCompletion 不看轮次恒返回停因 → 本条红。
    [Fact]
    public async Task S09_05_正常路径交给下一格的是最终文本且无停因()
    {
        var tool = CountingTool.NoArg("t", () => "工具结果");
        var model = new ScriptedChatClient().ToolCall("t").Reply("最终答复");
        var guard = Guard();

        var (run, text, _) = await DriveAsync(model, guard, 6, tool.Tool);

        Assert.Equal("最终答复", text);
        Assert.Null(run.Stop);
        Assert.Null(guard.InspectCompletion());
        Assert.Single(run.Traces);
        Assert.Equal("工具结果", run.Traces[0].Result);
        Assert.Null(run.Traces[0].Error);
        Assert.Equal(1, tool.Executions);
    }

    // [S09-06] 绿对照：防循环的阈值只数「连续」同参调用，中间夹了别的调用就要重来，
    // 否则正常的多轮推进会被误杀。这里 t 累计出现 3 次，但连续最多 2 次，不该终止。
    // 负控：把 DetectLoop 的 else 分支从「重置为 1」改成「继续累加」→ 本条红。
    [Fact]
    public async Task S09_06_夹了别的调用时重复计数重来()
    {
        var a = CountingTool.NoArg("t", () => "A");
        var b = CountingTool.NoArg("other", () => "B");
        var model = new ScriptedChatClient()
            .ToolCall("t").ToolCall("other").ToolCall("t").ToolCall("t")
            .Reply("完成");
        var guard = Guard(maxIterations: 6, maxRepeated: 3);

        var (run, text, _) = await DriveAsync(model, guard, 6, a.Tool, b.Tool);

        Assert.Equal("完成", text);
        Assert.Null(run.Stop);
        Assert.Equal(3, a.Executions);
        Assert.Equal(1, b.Executions);
    }

    // ── 装配 ────────────────────────────────────────────────────────

    /// <summary>
    /// 本组自己的回填内容提取器：反射取每条 AIContent 的 Result 属性（object，不是 string 属性）。
    /// 04 里的正常工具作绿对照，证明它不是恒返回空串。
    /// </summary>
    private static string ResultPayload(IReadOnlyList<ChatMessage> messages)
        => string.Concat(messages
            .SelectMany(m => m.Contents)
            .Select(c => c.GetType().GetProperty("Result")?.GetValue(c)?.ToString() ?? string.Empty));

    private static AgentRunGuard Guard(
        int maxIterations = 6,
        int maxRepeated = 3,
        int maxTokens = 0,
        IAgentUsageSource? usage = null)
        => new(new AgentLoopOptions
        {
            MaxIterations = maxIterations,
            MaxRepeatedToolCalls = maxRepeated,
            MaxTokensPerRun = maxTokens,
        }, usage);

    /// <summary>真实工具环 + 真实执行控制守卫；只有模型、工具、审批策略是替身。</summary>
    private static async Task<(ToolExecutionRun Run, string Text, Exception? Thrown)> DriveAsync(
        ScriptedChatClient model,
        AgentRunGuard guard,
        int maxIterations,
        params AITool[] tools)
    {
        var pipeline = PipelineDriver.NewPipeline(new FixedApprovalPolicy(ToolApprovalMode.Auto));
        var run = pipeline.Create(model, new ToolExecutionLimits(maxIterations, Guard: guard));

        var text = string.Empty;
        Exception? thrown = null;
        try
        {
            var response = await run.Client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "开始")],
                new ChatOptions { Tools = tools });
            text = response.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        return (run, text, thrown);
    }

    /// <summary>每次读取累计一个步长：构造守卫时取基线，判定时读数必然更高。</summary>
    private sealed class RisingUsage(long step) : IAgentUsageSource
    {
        private long _tokens;

        public AgentUsage GetUsage()
        {
            _tokens += step;
            return new AgentUsage(0, _tokens, 0);
        }
    }
}

using AgentWorkbench.TestKit;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Tools.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// S04（模型决策）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>边界</b>：本格只到「模型输出变成下一步」为止，测具是真实的
/// <see cref="ToolExecutionPipeline"/> 加脚本化模型（<see cref="ScriptedChatClient"/>），
/// 不碰真实模型。五步与覆盖情况：
/// <list type="number">
/// <item>装配工具清单：模型看到的恰好是白名单内的工具 —— 有</item>
/// <item>模型给工具调用时把它执行掉 —— 有</item>
/// <item>模型请求清单外的工具时不执行、且模型能收到「工具找不到」 —— 有</item>
/// <item>模型只给文本时原样交给 S10 —— 有</item>
/// <item><b>既无文本也无工具调用</b> —— <b>测不了</b>：工具环没有「成功/失败」这个概念，
/// 这条要装配整段 Harness 才能观察，归 S10 的流式出口</item>
/// </list>
/// 另有 S04_05：清单外的调用必须被记下来（回填发生在 MEA 内部，走不到函数执行器）。
///
/// <b>不在本格</b>：「说了但没做」的处置——本格只能保证「没调工具就没有动作」，
/// 界面怎么如实显示「0 个动作」归 S10。
/// </summary>
public sealed class S04ContractTests
{
    // [S04-01] 负控：删掉 ToolCatalog 里的 .Where(_policy.IsAllowed) → 本条红。
    [Fact]
    public void S04_01_模型只看到白名单内的工具()
    {
        var catalog = CreateCatalog(
            ["allowed_tool"],
            Tool("allowed_tool"),
            Tool("blocked_tool"));

        var names = catalog.GetToolNames();

        Assert.Equal(["allowed_tool"], names);
    }

    // [S04-02] 负控：FunctionInvoker 里不调 context.Function.InvokeAsync → 本条红（工具没被执行）。
    [Fact]
    public async Task S04_02_模型给工具调用时把它执行掉()
    {
        var tool = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient()
            .ToolCall("allowed_tool")
            .Reply("好了");

        var (run, _, _) = await DriveAsync(model, tool.Tool);

        Assert.Equal(1, tool.Executions);
        Assert.Single(run.Traces);
        Assert.Empty(run.UnknownCalls);   // 白名单内的调用不得被记成「未知」
        Assert.Equal("allowed_tool", run.Traces[0].Name);
    }

    // [S04-03] 模型请求一个不在清单里的工具名时：不得执行任何工具；
    // 但**不得抛异常**——MEA 会把「工具找不到」回填给模型，让模型自己换路
    // （`TerminateOnUnknownCalls` 默认 false，这是成熟做法：抛异常会让模型连错误都看不到）。
    // 负控：把 pipeline 里的 guard 换成「未知名字直接跳过且不继续」→ 本条红（模型收不到回填）。
    [Fact]
    public async Task S04_03_模型请求清单外的工具时不执行任何工具()
    {
        var allowed = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient()
            .ToolCall("not_in_whitelist")
            .Reply("那我换个方式回答");

        var (run, _, thrown) = await DriveAsync(model, allowed.Tool);

        // ① 清单外的工具一次都没被执行，轨迹里也不该出现它
        Assert.Equal(0, allowed.Executions);
        Assert.Empty(run.Traces);

        // ② 不抛异常：模型能看见「工具找不到」并据此继续，这是它自我纠正的唯一入口
        Assert.Null(thrown);
        Assert.Equal(2, model.CallCount);
        Assert.Contains("not_in_whitelist", RequestInspector.Dump(model.Requests[1]));
    }

    // [S04-05] 回填发生在 MEA 内部，走不到 FunctionInvoker——不单独记，事后就查不到
    // 「模型曾经请求过一个不存在的工具」。本条守的就是这一笔。
    // 负控：删掉 ToolExecutionPipeline.Create 里的 ToolCallGuardChatClient 包装 → 本条红。
    [Fact]
    public async Task S04_05_清单外的工具调用被记录下来()
    {
        var allowed = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient()
            .ToolCall("not_in_whitelist")
            .Reply("那我换个方式回答");

        var (run, _, _) = await DriveAsync(model, allowed.Tool);

        Assert.Equal(["not_in_whitelist"], run.UnknownCalls);
    }

    // [S04-04] 负控：把模型返回的文本丢掉（只取轨迹里的最后一条）→ 本条红。
    [Fact]
    public async Task S04_04_模型只给文本时原样交给下一格()
    {
        var tool = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient().Reply("  纯文本答复  ");

        var (run, text, _) = await DriveAsync(model, tool.Tool);

        Assert.Empty(run.Traces);
        Assert.Equal("  纯文本答复  ", text);
    }

    // [S04-06] 生产路径是流式（Hub 走 StreamAsync），但 guard 的流式分支此前零覆盖：
    // 非流式测绿了也说明不了流式对。本条把流式这半也钉住。
    // 负控：删掉 ToolCallGuardChatClient.GetStreamingResponseAsync 里的 seen/Report → 本条红。
    [Fact]
    public async Task S04_06_流式响应里的清单外工具调用也被记录()
    {
        var allowed = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient()
            .ToolCall("not_in_whitelist")
            .Reply("那我换个方式回答");

        var run = await DriveStreamingAsync(model, allowed.Tool);

        Assert.Equal(0, allowed.Executions);
        Assert.Equal(["not_in_whitelist"], run.UnknownCalls);
    }

    // [S04-07] 流式同一次调用会分片到达，名字只在首片非空。整轮结束再判，
    // 才不会被半截名字或重复分片记成两笔。本条锁住「一次调用只记一笔」。
    // 负控：把整轮结束后的报告改成逐片立即报告 → 本条红（会记两笔）。
    [Fact]
    public async Task S04_07_同一次调用的多个分片只记一笔()
    {
        var allowed = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient()
            .ToolCallFragmented("not_in_whitelist")
            .Reply("那我换个方式回答");

        var run = await DriveStreamingAsync(model, allowed.Tool);

        Assert.Equal(["not_in_whitelist"], run.UnknownCalls);
    }

    // ── 装配 ────────────────────────────────────────────────────────

    /// <summary>走流式入口跑一次工具环：生产路径是流式，非流式绿不代表这条绿。</summary>
    private static async Task<ToolExecutionRun> DriveStreamingAsync(ScriptedChatClient model, AITool tool)
    {
        var pipeline = PipelineDriver.NewPipeline(new FixedApprovalPolicy(ToolApprovalMode.Auto));
        var run = pipeline.Create(model, new ToolExecutionLimits(MaxIterations: 4));

        await foreach (var update in run.Client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "开始")],
                           new ChatOptions { Tools = [tool] }))
        {
            GC.KeepAlive(update);   // 只为把流读完；断言看 run 上的痕迹
        }

        return run;
    }

    private static AITool Tool(string name)
        => AIFunctionFactory.Create(() => "x", name, "测试工具");

    /// <summary>真 ToolCatalog + 真白名单判定；工具提供方用假的，按白名单过滤后在目录里可见。</summary>
    private static ToolCatalog CreateCatalog(IReadOnlyList<string> whitelist, params AITool[] allowed)
    {
        var services = new ServiceCollection();
        var provider = new Mock<IAgentToolProvider>();
        provider.Setup(p => p.GetTools()).Returns(allowed.ToList());
        services.AddSingleton(provider.Object);

        var policy = new Mock<IToolPolicy>();
        policy.SetupGet(p => p.Whitelist).Returns(whitelist.ToList());
        policy.Setup(p => p.IsAllowed(It.IsAny<string>()))
              .Returns<string>(name => whitelist.Contains(name, StringComparer.Ordinal));

        return new ToolCatalog(
            services.BuildServiceProvider(),
            policy.Object,
            NullLogger<ToolCatalog>.Instance);
    }

    /// <summary>跑一次工具环，返回轨迹、最终文本与异常。异常不外抛——S04_03 需要把「抛了」也当成一种结果。</summary>
    private static async Task<(ToolExecutionRun Run, string Text, Exception? Thrown)> DriveAsync(
        ScriptedChatClient model, AITool tool, bool includeTool = true)
    {
        var pipeline = PipelineDriver.NewPipeline(new FixedApprovalPolicy(ToolApprovalMode.Auto));
        var run = pipeline.Create(model, new ToolExecutionLimits(MaxIterations: 4));

        var text = string.Empty;
        Exception? thrown = null;
        try
        {
            var response = await run.Client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "开始")],
                new ChatOptions { Tools = includeTool ? [tool] : [] });
            text = response.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        return (run, text, thrown);
    }
}

using System.Text.Json;
using AgentWorkbench.TestKit;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Tools.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// S04（模型决策）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>边界</b>：本格起于模型响应，止于「模型输出变成下一步」。测具是真实的
/// <see cref="ToolExecutionPipeline"/> / <see cref="ToolCatalog"/> 加脚本化模型
/// （<see cref="ScriptedChatClient"/>），不碰真实模型。文档四栏的覆盖：
/// <list type="bullet">
/// <item>「白名单外的工具不出现在模型清单里」→ 01、08–13</item>
/// <item>「输出只被解析成工具调用或纯文本，中间不丢不添」→ 02–04、14–15</item>
/// <item>「模型请求清单外工具」不执行、能被回填、且留痕 → 03、05–07</item>
/// <item><b>「既无文本也无工具调用时不得当成本轮成功」——测不了</b>：工具环没有
/// 「成功 / 失败」这个概念，这条要装配整段 Harness 的流式出口才能观察，归 S10。
/// 不写占位测试、也不写恒真的绿；S1 那条 Skip 的用法不在这里复用。</item>
/// </list>
/// 「说了但没做」的处置归 S10（界面如实显示「0 个动作」），本格只保证「没调工具就没有动作」。
///
/// <b>一处必须说明的替换</b>：01–07 里的 <c>IToolPolicy</c> 是 Mock，验的是目录与守卫，
/// <b>真实的 <c>ToolPolicy.IsAllowed</c> 写错，它们照样全绿</b>。08–11 换成真实策略 +
/// 真实目录，把最后一层假件去掉——这是本格此前最系统的一处假绿。
///
/// <b>层 × 方向</b>：01–07 是工具环与守卫（Core，执行）；08–13 是白名单策略与目录（Core，准入）；
/// 14–15 是输出解析（Core，解析）。全格没有一步属 Host，也没有一步属域。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
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

    // ── 真实白名单策略：01–07 全用 Mock，这里把策略本体换成真的 ────────────

    // [S04-08] 精确名匹配。负控：把 ToolPolicy.IsAllowed 改成「无条件放行」或忽略大小写
    // → 后四行红。白名单是安全边界，大小写不敏感会让 `Calculator` 混进来。
    [Theory]
    [InlineData("calculator", "calculator", true)]
    [InlineData("calculator", "other", false)]
    [InlineData("calculator", "", false)]
    [InlineData("calculator", "Calculator", false)]
    [InlineData("   ", "anything", false)]
    public void S04_08_真实策略只放行精确名单内的名字(string rule, string name, bool expected)
        => Assert.Equal(expected, RealPolicy(rule).IsAllowed(name));

    // [S04-09] 只支持「尾部 *」这一种通配。负控：把 StartsWith 换成 Contains（模糊匹配）
    // → 第 3 行红；把中间通配也实现出来 → 第 4 行红。两者都会让读配置的人答不出
    // 「到底放行了哪些工具」，等于没有白名单（ToolPolicy.cs 的注释即此意）。
    [Theory]
    [InlineData("mcp__rag__*", "mcp__rag__search", true)]
    [InlineData("mcp__rag__*", "mcp__other__search", false)]
    [InlineData("rag__*", "mcp__rag__search", false)]
    [InlineData("*document*", "get_document", false)]
    public void S04_09_尾部星号只按前缀放行(string rule, string name, bool expected)
        => Assert.Equal(expected, RealPolicy(rule).IsAllowed(name));

    // [S04-10] 配置缺失 / 白名单写空：一个工具都看不见，而不是「不配就全放行」。
    // 负控：让 IsAllowed 在空表时返回 true，或删掉 ToolCatalog 里的 .Where → 本条红。
    [Fact]
    public void S04_10_空白名单不放行任何工具()
    {
        var policy = RealPolicy();
        var catalog = BuildCatalog(policy, Tool("calculator"), Tool("mcp__rag__search"));

        Assert.Empty(catalog.GetTools());
        Assert.Empty(catalog.GetToolNames());
        Assert.Empty(policy.Allowed(["calculator", "mcp__rag__search"]));
    }

    // [S04-11] 把「配置 → 真实策略 → 目录 → 模型工具清单」整条链接起来验，
    // 不再是 Mock 策略 + 目录各验一半。负控：删掉 ToolCatalog 的 .Where(_policy.IsAllowed) → 本条红。
    [Fact]
    public void S04_11_真实策略接进目录后只放行名单内的工具()
    {
        var policy = RealPolicy("calculator", "mcp__rag__*");
        var catalog = BuildCatalog(
            policy,
            Tool("calculator"),
            Tool("mcp__rag__search"),
            Tool("mcp__other__write"),
            Tool("list_documents"));

        Assert.Equal(["calculator", "mcp__rag__search"], catalog.GetToolNames());
    }

    // ── 目录自身的两条契约（文档「按名称去重」「汇总所有提供方」） ──────────

    // [S04-12] 同名工具只登记一次：模型看到两个同名工具会让调用结果不可判定。
    // 负控：删掉 ToolCatalog 里的 .DistinctBy → 本条红。
    [Fact]
    public void S04_12_同名工具只登记一次()
    {
        var catalog = BuildCatalog(MockPolicy(["dup"]), Tool("dup"), Tool("dup"));

        Assert.Single(catalog.GetTools());
        Assert.Equal(["dup"], catalog.GetToolNames());
    }

    // [S04-13] 工具来自多个 IAgentToolProvider，过滤发生在汇总之后。
    // 负控：把 SelectMany 改成只取第一个 provider → 本条红（只放行第二个时结果为空）。
    [Fact]
    public void S04_13_多个提供方汇总后统一过滤()
    {
        var policy = MockPolicy(["from_second"]);
        var catalog = BuildCatalogFromProviders(
            policy,
            new List<AITool> { Tool("from_first") },
            new List<AITool> { Tool("from_second") });

        Assert.Equal(["from_second"], catalog.GetToolNames());
    }

    // ── 输出「中间不丢不添」：02 只验了工具被执行，没验参数与结果 ──────────

    // [S04-14] 一次工具调用的形状要完整进轨迹：参数、结果、无错误、未截断。
    // 负控：BeginCall 丢掉 Arguments，或 CompleteCall 不写 Result / 不置 Truncated → 本条红。
    [Fact]
    public async Task S04_14_工具调用的参数与结果原样进轨迹()
    {
        var tool = CountingTool.OneArg("allowed_tool", input => $"已查到 {input}");
        var model = new ScriptedChatClient()
            .ToolCall("allowed_tool", new Dictionary<string, object?> { ["input"] = "PO-1" })
            .Reply("好了");

        var (run, _, _) = await DriveAsync(model, tool.Tool);

        Assert.Equal(1, tool.Executions);
        Assert.Single(run.Traces);
        var trace = run.Traces[0];
        Assert.Equal("allowed_tool", trace.Name);
        using var args = JsonDocument.Parse(trace.Arguments!);
        Assert.Equal("PO-1", args.RootElement.GetProperty("input").GetString());
        Assert.Equal("已查到 PO-1", trace.Result);
        Assert.Null(trace.Error);
        Assert.False(trace.Truncated);
    }

    // [S04-15] 工具结果必须回到模型，否则模型会继续猜、或把没做的事说成做了。
    // 与 03 的「工具找不到回填」正好是两种回程，各验一半。
    // 负控：FunctionInvoker 不把结果 return 给 MEA（改成 return null）→ 本条红。
    [Fact]
    public async Task S04_15_工具结果回填给模型()
    {
        var tool = CountingTool.NoArg("allowed_tool", () => "工具结果");
        var model = new ScriptedChatClient()
            .ToolCall("allowed_tool")
            .Reply("好了");

        var (run, _, _) = await DriveAsync(model, tool.Tool);

        Assert.Equal(1, tool.Executions);
        Assert.Equal(2, model.CallCount);
        var results = model.Requests[1]
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();
        Assert.Single(results);
        Assert.Equal("call_1", results[0].CallId);
        Assert.Contains("工具结果", results[0].Result?.ToString());
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

    /// <summary>
    /// 01–07 用的装配：真 ToolCatalog + <b>Mock 策略</b>；工具提供方也是假的。
    /// 它验的是目录的裁剪与守卫，不验「白名单到底放行什么」——后者见 08–11。
    /// </summary>
    private static ToolCatalog CreateCatalog(IReadOnlyList<string> whitelist, params AITool[] allowed)
        => BuildCatalog(MockPolicy(whitelist), allowed);

    /// <summary>真实 <see cref="ToolPolicy"/>：Mock 的只是配置来源，匹配逻辑一行不替。</summary>
    private static IToolPolicy RealPolicy(params string[] rules)
    {
        var monitor = new Mock<IOptionsMonitor<ToolPolicyOptions>>();
        monitor.SetupGet(m => m.CurrentValue)
               .Returns(new ToolPolicyOptions { Whitelist = [.. rules] });
        return new ToolPolicy(monitor.Object);
    }

    private static IToolPolicy MockPolicy(IReadOnlyList<string> whitelist)
    {
        var policy = new Mock<IToolPolicy>();
        policy.SetupGet(p => p.Whitelist).Returns(whitelist.ToList());
        policy.Setup(p => p.IsAllowed(It.IsAny<string>()))
              .Returns<string>(name => whitelist.Contains(name, StringComparer.Ordinal));
        return policy.Object;
    }

    /// <summary>一个提供方 + 指定策略；目录本体是真的。</summary>
    private static ToolCatalog BuildCatalog(IToolPolicy policy, params AITool[] available)
        => BuildCatalogFromProviders(policy, available.ToList());

    /// <summary>多个提供方各自注册，用来验「汇总之后再过滤」。</summary>
    private static ToolCatalog BuildCatalogFromProviders(
        IToolPolicy policy, params IReadOnlyList<AITool>[] providers)
    {
        var services = new ServiceCollection();
        foreach (var tools in providers)
        {
            var provider = new Mock<IAgentToolProvider>();
            provider.Setup(p => p.GetTools()).Returns(tools.ToList());
            services.AddSingleton(provider.Object);
        }

        return new ToolCatalog(
            services.BuildServiceProvider(),
            policy,
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

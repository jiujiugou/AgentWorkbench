using System.Security.Claims;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Shared;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S10（流式输出 + 落库）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>本组与模块级单测不关联</b>：轨迹落库、终态 / 归类码、用量、截断、不完整性这些在
/// <c>CoreTests/Engineering/Observability</c> 已有整套模块级单测；历史一致性另有
/// <c>HistoryDivergenceTests</c>。那两套和本组互不迁移、互不复用、互不计数。
/// S 系列是「一次 run 的流程验收」，与其余 Sxx 同处 Host 项目、走门禁默认项目；
/// <c>scripts/stage_gate.py</c> 不因本格归属改动。
///
/// <b>边界</b>：起于 <c>AgentHub</c> 收到一次请求，止于终态事件与三笔落库对齐。四栏覆盖：
/// ①审计写失败不算成功 → 06（宿主侧）；②流式中断不让前端以为完成 → 03、06；
/// ③三笔落库对不上 → 04、05；「交给下一格什么」→ 02；
/// 「本轮的动作用量必须如实可见 / 0 个动作」→ 01。
/// <b>不归本格</b>：动作本身的正确性（S05–S08）。
///
/// <b>登记在案的空白（不写占位测试、不写恒真的绿）</b>：
/// <list type="number">
/// <item><b>实测发现</b>：<c>HarnessStreamEventKind.Failed</c> 被 yield 出来后，
/// <c>await foreach</c> 不会中断，统一收尾仍会发一次 <c>ReceiveDone</c>（此时 <c>run = null</c>）。
/// 03 只验「终态通知 + 归类码先于收尾发出」；若前端只认 <c>ReceiveDone</c> 就会把中止误判成完成，
/// 文档怕什么② <b>尚未完全闭合</b>，未修。</item>
/// <item>「审计写失败 → 不算成功」：Harness 正常路径的 <c>IExecutionTraceWriter.SaveAsync</c>
/// 未 catch，抛异常会冒泡到 Hub 的 catch；但失败 / 取消路径的 <c>PersistQuietlyAsync</c>
/// catch 后只记 Warning。本组通过 <c>IAgentHarness</c> 抛异常模拟宿主侧，
/// <b>没有直接注入写口让它失败</b>，存储侧不在这里验。</item>
/// <item>三笔落库只验到「对话 user / assistant 消息 + runId 与 run 摘要对齐」；
/// 动作审计（<c>ITraceSink</c>）与执行轨迹（<c>IExecutionTraceWriter</c>）的真实落库不碰。</item>
/// <item><c>S01_01</c> 的前端编号装置 Skip 依旧挂着，本格不代替它。</item>
/// </list>
///
/// <b>层 × 方向</b>：01–06 全在 Host（<c>AgentHub</c>）；Harness、会话仓储、SignalR 客户端、
/// 身份作用域全是 Mock，不碰真实连接与数据库。事件用本地 async 迭代器喂进去。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S10ContractTests
{
    private static readonly string[] ExpectedDeltas = ["流式", "拼接"];

    // [S10-01] 文档「本轮的动作用量必须如实可见：没有执行任何动作时，界面要能显示 0 个动作」。
    // 这是 S04 那条「纯文本谎报执行」的落点。绿对照给 1 个工具步，防止断言恒真。
    // 负控：把 ReceiveDone 里的 tools 改成 run.Steps.Count（把 assistant 步算进去）→ 0 那行红。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task S10_01_动作用量如实可见零动作与一动作(int toolSteps)
    {
        var (hub, harness, _, sent, _) = BuildHub();
        SetupStream(harness, Events(Completed(toolSteps: toolSteps)));

        await hub.Send(new ChatRequest(null, "查一下", "m-s10-01"));

        var run = RunSummary(sent);
        Assert.Equal(toolSteps.ToString(), Str(run, "tools"));
        Assert.Equal((toolSteps + 1).ToString(), Str(run, "steps"));
    }

    // [S10-02] 「交给下一格什么」：终态 + 归类码必须随 ReceiveDone 下发，
    // 否则界面上「答完了」和「跑满迭代被强制收口」长得一样。
    // 负控：删掉 ReceiveDone 里的 status / code 字段 → 本条红。
    [Fact]
    public async Task S10_02_终态与归类码随完成事件下发()
    {
        var (hub, harness, _, sent, _) = BuildHub();
        SetupStream(harness, Events(Completed(
            status: AgentRunStatus.IterationLimit,
            code: AgentRunCode.IterationLimit)));

        await hub.Send(new ChatRequest(null, "查一下", "m-s10-02"));

        var run = RunSummary(sent);
        Assert.Equal(AgentRunStatus.IterationLimit, Str(run, "status"));
        Assert.Equal(AgentRunCode.IterationLimit, Str(run, "code"));
    }

    // [S10-03] 失败轮要给出「以什么状态结束、为什么」，且终态通知先于统一收尾，
    // 否则「用户取消」和「服务出错」在界面上长得一样。
    // 负控：删掉 NotifyStoppedAsync，或把 status / code 置空 → 本条红。
    // 边界：Failed 之后 ReceiveDone 仍会发（run=null），见头部登记 1。
    [Fact]
    public async Task S10_03_失败轮先发终态通知且带状态与归类码()
    {
        var (hub, harness, _, sent, _) = BuildHub();
        SetupStream(harness, Events(new HarnessStreamEvent(
            HarnessStreamEventKind.Failed,
            Error: "生成失败", Status: AgentRunStatus.Timeout, Code: AgentRunCode.RunTimeout)));

        await hub.Send(new ChatRequest(null, "查一下", "m-s10-03"));

        var stopped = sent.Single(e => e.Method == "ReceiveStopped");
        Assert.Equal(AgentRunStatus.Timeout, Str(Payload(stopped), "status"));
        Assert.Equal(AgentRunCode.RunTimeout, Str(Payload(stopped), "code"));
        Assert.Contains(sent, e => e.Method == "Error");
        Assert.True(
            sent.IndexOf(stopped) < sent.FindIndex(e => e.Method == "ReceiveDone"),
            "终态通知必须先于统一收尾事件");
    }

    // [S10-04] 「三笔落库对不上」能验的那一半：user 消息、assistant 消息（带 runId）、
    // 完成事件里的 runId 三者对得上；界面才能顺着 runId 找回当时的执行过程。
    // 负控：SaveAnswerAsync 不把 runId 传进 AddMessageAsync → 本条红。
    [Fact]
    public async Task S10_04_对话落库与run摘要的runId对齐()
    {
        var (hub, harness, _, sent, messages) = BuildHub();
        SetupStream(harness, Events(
            new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "答复"),
            Completed(runId: 77, text: "答复")));

        await hub.Send(new ChatRequest(null, "查一下", "m-s10-04"));

        Assert.Contains(messages, m => m is { Role: "user", Id: "conv-1", Text: "查一下" });
        var assistant = messages.Single(m => m.Role == "assistant");
        Assert.Equal("答复", assistant.Text);
        Assert.Equal(77L, assistant.RunId);
        Assert.Equal("77", Str(RunSummary(sent), "runId"));
    }

    // [S10-05] 流式增量拼出来的答复，必须与最终落库的文本一致；
    // 不能拿 HarnessRunResult.Text 另存一份（这里故意让两者不同，防恒真）。
    // 负控：SaveAnswerAsync 改用 run?.Text → 本条红。
    [Fact]
    public async Task S10_05_流式增量与最终落库文本一致()
    {
        var (hub, harness, _, sent, messages) = BuildHub();
        SetupStream(harness, Events(
            new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "流式"),
            new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "拼接"),
            Completed(runId: 78, text: "模型最终文本")));

        await hub.Send(new ChatRequest(null, "查一下", "m-s10-05"));

        var deltas = sent.Where(e => e.Method == "ReceiveDelta")
            .Select(e => Str(Payload(e), "delta")).ToArray();
        Assert.Equal(ExpectedDeltas, deltas);
        Assert.Equal("流式拼接", messages.Single(m => m.Role == "assistant").Text);
        Assert.NotEqual("模型最终文本", messages.Single(m => m.Role == "assistant").Text);
    }

    // [S10-06] 流式中途抛异常（含轨迹写失败冒泡）：不发 ReceiveDone，
    // 界面不能把半截输出显示成「完成」；已收到的增量仍如实落库。
    // 负控：在 Hub 的 catch 分支里补发 ReceiveDone → 本条红。
    [Fact]
    public async Task S10_06_流式中途失败不发完成事件且半截答复仍落库()
    {
        var (hub, harness, _, sent, messages) = BuildHub();
        SetupStream(harness, ThrowingEvents());

        await hub.Send(new ChatRequest(null, "查一下", "m-s10-06"));

        Assert.Contains(sent, e => e.Method == "Error");
        Assert.DoesNotContain(sent, e => e.Method == "ReceiveDone");
        Assert.Contains(messages, m => m is { Role: "assistant", Text: "半截答复" });
    }

    // ── 装配 ────────────────────────────────────────────────────────

    private sealed record Sent(string Method, object?[] Args);

    private static (
        AgentHub Hub,
        Mock<IAgentHarness> Harness,
        Mock<IConversationRepository> Conversations,
        List<Sent> SentEvents,
        List<(string Id, string Role, string Text, long? RunId)> Messages) BuildHub()
    {
        var sent = new List<Sent>();
        var messages = new List<(string Id, string Role, string Text, long? RunId)>();

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = "conv-1", TenantId = "tenant-1", UserId = "user-1" });
        conversations.Setup(c => c.AddMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>()))
            .Callback<string, string, string, long?>((id, role, text, runId) => messages.Add((id, role, text, runId)))
            .Returns(Task.CompletedTask);

        var scope = new Mock<IActionSubjectScope>();
        var caller = new Mock<ISingleClientProxy>();
        caller.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((m, a, _) => sent.Add(new Sent(m, a)))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(caller.Object);

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-s10");
        context.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);
        context.SetupGet(c => c.User).Returns(Principal());

        var hub = new AgentHub(
            harness.Object,
            conversations.Object,
            Options.Create(new ProviderOptions()),
            Options.Create(new KnowledgeOptions()),
            scope.Object,
            NullLogger<AgentHub>.Instance)
        {
            Context = context.Object,
            Clients = clients.Object,
        };

        return (hub, harness, conversations, sent, messages);
    }

    private static void SetupStream(Mock<IAgentHarness> harness, IAsyncEnumerable<HarnessStreamEvent> events)
        => harness.Setup(h => h.StreamAsync(
                It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => events);

    private static HarnessStreamEvent Completed(
        string status = AgentRunStatus.Succeeded,
        string? code = null,
        int toolSteps = 0,
        string text = "答复",
        long runId = 77)
    {
        var steps = new List<HarnessStep>();
        for (var i = 0; i < toolSteps; i++)
            steps.Add(new HarnessStep(i, "tool", $"t{i}", "{}", "ok"));
        steps.Add(new HarnessStep(toolSteps, "assistant", null, null, text));

        var result = new HarnessRunResult(
            runId, "conv-1", text, steps.Count, "model-a", 12, steps, status, code);
        return new HarnessStreamEvent(HarnessStreamEventKind.Completed, Result: result);
    }

    private static async IAsyncEnumerable<HarnessStreamEvent> Events(params HarnessStreamEvent[] items)
    {
        foreach (var e in items)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static async IAsyncEnumerable<HarnessStreamEvent> ThrowingEvents()
    {
        await Task.Yield();
        yield return new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "半截答复");
        throw new InvalidOperationException("trace write failed");
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "user-1"),
        new Claim(CurrentUser.TenantIdClaim, "tenant-1"),
        new Claim(ClaimTypes.Name, "wang"),
    ], "test"));

    // 事件 payload 是匿名对象，用反射取字段；不做源码字符串断言。
    private static object Payload(Sent e) => e.Args[0]!;

    private static object? Prop(object target, string name)
        => target.GetType().GetProperty(name)?.GetValue(target);

    private static string? Str(object target, string name) => Prop(target, name)?.ToString();

    private static object RunSummary(List<Sent> sent)
    {
        var done = sent.Single(e => e.Method == "ReceiveDone");
        var run = Prop(Payload(done), "run");
        Assert.NotNull(run);
        return run!;
    }
}

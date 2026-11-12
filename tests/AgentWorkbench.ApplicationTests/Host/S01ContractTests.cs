using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Shared;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S01（你输入一句话）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>测不了的不藏起来</b>：第 1 步发生在浏览器里，本仓没有可跑的前端装置，
/// 所以它是一条 <see cref="Skip"/>。它在列表里可见、每次跑都提醒，并让 <c>stage_gate.py</c>
/// 报 BLOCKED——「没有保障」不得伪装成「有保障」。
///
/// <b>编号 07 空缺是故意的</b>：第 7 步（同连接拒绝第二次发送）按文档切点归 S02
/// （S02「止于 <c>StreamAsync</c> 之前」），断言在 S02_08，本类不重复写第二份。
///
/// <b>哨兵</b>：名字带 <c>Sentinel</c> 的不是客户会受的伤，只提醒「改了要同步文档」，
/// 由 <c>stage_gate.py</c> 的 filter 排除，不计入这一格。
///
/// <b>层 × 方向（九步的坐标）</b>：本段只写这一格在 Host / Core 边界上的位置，
/// 不重复方法名的「功能」，也不重复文档里的「怕什么 / 不许留下什么」。
///
/// 判层：<b>把网页换成命令行或定时任务，这一步还成立吗</b>。
/// 成立 → <c>Core</c>；不成立 → <c>Host</c>；两边都不纯、正在换形状 → <c>边界</c>。
/// 这与 <c>docs/归属判据.md</c> 第 1 条不是同一把尺子：那条「换域还成立吗」判 Core / 域，
/// 这条「换入口还成立吗」判 Host / Core。
///
/// 判方向：<c>请求</c> = 值往里送；<c>结果</c> = 值往外送；
/// <c>闸门</c> = 值没走，只决定放不放行 / 释放不释放。
///
/// 01 点击生成编号　　　　　　Host　　请求
/// 02 组装成请求　　　　　　　Host　　请求
/// 03 缺编号拒绝　　　　　　　Host　　闸门
/// 04 空输入不进后续　　　　　Host　　闸门
/// 05 文本原样交给模型　　　　边界　　请求　<c>ChatRequest.Text</c> → 落库 user 消息 + <c>HarnessRunRequest.Input</c>
/// 06 编号带进身份作用域　　　边界　　请求　<c>clientMessageId</c> → <c>IActionSubjectScope.Begin</c>
/// 07 （空缺，归 S02）　　　　—　　　—
/// 08 失败后释放连接　　　　　Host　　闸门
/// 09 同编号跨连接各跑一次　　Host　　不参与（哨兵）
///
/// <b>结论</b>：九步里没有一步属 <c>Core</c>。S01 全在「进去」那半——请求在 Host 成形、
/// 边界换形状，Core 从 <c>AgentHub</c> 调 <c>StreamAsync</c> 才开始（S02 / S03）；
/// 结果侧在 S10 的 <c>ReceiveDelta</c> 回显，同样落在 Host。
///
/// 02 采用「值里还有网页味道」的判法：<c>ChatRequest</c> 是 SignalR 入参，换入口就得重拼。
/// 若改按「离开浏览器即进内核」的位置判，02 / 05 / 06 必须一起改成「边界」，不得只改一格。
///
/// 纪律：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S01ContractTests
{
    // [S01-01] 边界：编号生成发生在 web/src/stores/agent.ts（crypto.randomUUID），
    // 本仓测试跑在后端，看不到浏览器行为。web/tests/*.spec.ts 是 v1 遗留（依赖 vitest，
    // package.json 既没依赖也没 test 脚本），跑不起来。要转绿，先让它能跑。
    [Fact(Skip = "无装置：编号生成发生在前端 ChatView，本仓没有可跑的浏览器测试环境")]
    public void S01_01_点击时生成本次输入编号() =>
        Assert.Fail("这一格没有装置。要转绿，先让 web/tests 能跑，再断言编号在重试间保持不变。");

    // [S01-02] 负控：把 ClientMessageId 改成可空 / 给默认值 / 删掉任一位置 → 本条红。
    [Fact]
    public void S01_02_把一次输入组装成请求()
    {
        var ctor = typeof(ChatRequest).GetConstructors().Single();
        var ps = ctor.GetParameters();

        Assert.Equal(
            new[] { "ConversationId", "Text", "History", "ClientMessageId" },
            ps.Select(p => p.Name));
        Assert.Equal(typeof(string), ps[1].ParameterType);
        Assert.Equal(typeof(string), ps[3].ParameterType);
        Assert.False(ps[3].HasDefaultValue);
    }

    // [S01-03] 负控：在缺编号的拒绝路径里补发 ReceiveStopped / ReceiveDone → 本条红。
    [Fact]
    public async Task S01_03_缺编号时拒绝并提示()
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-s01-03");

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, string.Empty));

        AssertErrorOnce(caller);
        AssertNoSideEffects(harness, conversations, scope);
        AssertNoTerminalState(caller);
    }

    // [S01-04] 边界：前端已 trim 拦一道，这里验的是「绕过前端直连 Hub 也拦得住」。
    // 负控：删掉 AgentHub.RunAsync 里的空文本判空 → 本条红。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task S01_04_空输入时不进后续(string text)
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-s01-04-" + text.Length);
        SetReady(harness, conversations, "conv-s01-04");
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-if-not-blocked"));

        await hub.Send(new ChatRequest(null, text, null, "m-s01-04"));

        AssertNoSideEffects(harness, conversations, scope);
        AssertNoTerminalState(caller);
    }

    // [S01-05] 负控：Hub 里对 request.Text 做 Trim / 替换 → 本条红
    // （改了会让界面消息与模型输入不是同一句话）。
    [Fact]
    public async Task S01_05_把文本原样交给模型()
    {
        const string text = "  查一下 PO-1  ";
        var (hub, harness, conversations, _, _) = BuildHub("conn-s01-05");
        HarnessRunRequest? captured = null;

        SetReady(harness, conversations, "conv-s01-05");
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<HarnessRunRequest, CancellationToken>((r, _) => captured = r)
            .Throws(new InvalidOperationException("probe-stop-after-request"));

        await hub.Send(new ChatRequest(null, text, null, "m-s01-05"));

        conversations.Verify(c => c.AddMessageAsync("conv-s01-05", "user", text), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal(text, captured.Input);
    }

    // [S01-06] 边界（2026-11-11 实测）：编号只到作用域——HarnessRunRequest 只有
    // SessionId / Input / Instructions / Approver，没有编号字段。工具环取编号走
    // IActionSubjectScope → IActionOriginProvider，不经过 run 请求。
    // 负控：Begin 少传 clientMessageId（改成随机值 / 空）→ 本条红。
    [Fact]
    public async Task S01_06_把编号带进身份作用域()
    {
        const string messageId = "m-s01-06";
        var (hub, harness, conversations, scope, _) = BuildHub("conn-s01-06");

        SetReady(harness, conversations, "conv-s01-06");
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-after-scope"));

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, messageId));

        scope.Verify(s => s.Begin(It.IsAny<string?>(), "conv-s01-06", messageId), Times.Once);
    }

    // 第 7 步（同连接拒绝第二次发送）归 S02：文档 S02 明写「止于 StreamAsync 之前」，
    // 连接闸在这个范围内。黑盒断言见 S02_08，此处不重复。

    // [S01-08] 负控：删掉 RunAsync 的 finally 里 Generations.TryRemove → 本条红
    // （第二次进不到探活，用户永远卡在「生成中」）。
    [Fact]
    public async Task S01_08_失败后释放连接()
    {
        var (hub, harness, conversations, _, _) = BuildHub("conn-s01-08");
        SetReady(harness, conversations, "conv-s01-08");
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("invoke-failed"));

        await hub.Send(new ChatRequest(null, "第一句", null, "m-s01-08a"));
        await hub.Send(new ChatRequest(null, "第二句", null, "m-s01-08b"));

        // 探活发生在占闸之后：两次都到探活，说明第二次没被连接闸挡住。
        harness.Verify(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // [S01-09] 边界：记录现状，不是判据——当前只有连接级 Generations，没有输入级去重，
    // 所以同一编号跨两条连接会各跑一次。文档「未决约束」要求开重放前先补去重（键 = 认证主体 + 编号）。
    // 负控：在 Hub 里加一层按 (subject, clientMessageId) 的去重 → 本条红，提示必须同步改文档。
    [Fact]
    public async Task S01_09_Sentinel_同一编号跨连接各跑一次()
    {
        const string messageId = "m-s01-09";
        var (hubA, harnessA, conversationsA, _, _) = BuildHub("conn-s01-09a");
        var (hubB, harnessB, conversationsB, _, _) = BuildHub("conn-s01-09b");
        SetReady(harnessA, conversationsA, "conv-s01-09a");
        SetReady(harnessB, conversationsB, "conv-s01-09b");
        harnessA.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-a"));
        harnessB.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-b"));

        await hubA.Send(new ChatRequest(null, "同一句话", null, messageId));
        await hubB.Send(new ChatRequest(null, "同一句话", null, messageId));

        harnessA.Verify(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        harnessB.Verify(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 装配 / 断言 ────────────────────────────────────────────────

    private static void SetReady(
        Mock<IAgentHarness> harness,
        Mock<IConversationRepository> conversations,
        string conversationId)
    {
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord
            {
                Id = conversationId,
                TenantId = "tenant-1",
                UserId = "user-1",
            });
    }

    private static void AssertErrorOnce(Mock<ISingleClientProxy> caller)
        => caller.Verify(
            c => c.SendCoreAsync("Error", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);

    private static void AssertNoTerminalState(Mock<ISingleClientProxy> caller)
    {
        foreach (var terminal in new[] { "ReceiveStopped", "ReceiveDone", "ReceiveRefusal" })
        {
            caller.Verify(
                c => c.SendCoreAsync(terminal, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }

    private static void AssertNoSideEffects(
        Mock<IAgentHarness> harness,
        Mock<IConversationRepository> conversations,
        Mock<IActionSubjectScope> scope)
    {
        harness.Verify(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Verify(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        conversations.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        conversations.Verify(c => c.AddMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        scope.Verify(s => s.Begin(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "user-1"),
        new Claim(CurrentUser.TenantIdClaim, "tenant-1"),
        new Claim(ClaimTypes.Name, "wang"),
    ], "test"));

    private static (
        AgentHub Hub,
        Mock<IAgentHarness> Harness,
        Mock<IConversationRepository> Conversations,
        Mock<IActionSubjectScope> Scope,
        Mock<ISingleClientProxy> Caller) BuildHub(string connectionId)
    {
        var harness = new Mock<IAgentHarness>();
        var conversations = new Mock<IConversationRepository>();
        var scope = new Mock<IActionSubjectScope>();
        var caller = new Mock<ISingleClientProxy>();
        caller.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(caller.Object);

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
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

        return (hub, harness, conversations, scope, caller);
    }
}

using System.Collections.Concurrent;
using System.Reflection;
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
/// S02（Hub：验身份、取会话）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>边界</b>：本格止于 <c>StreamAsync</c> 之前（文档 S02 原文）。所以连接闸、身份、会话、
/// 作用域、探活、断开都在这里；落用户消息的文本由 S01_05 验，不在此重复。
/// </summary>
public sealed class S02ContractTests
{
    // [S02-01] 负控：删掉 RunAsync 的 IsAuthenticated 判空 → 本条红。
    [Fact]
    public async Task S02_01_未登录时拒绝()
    {
        var (hub, harness, conversations, scope, caller) =
            BuildHub("conn-s02-01", new ClaimsPrincipal(new ClaimsIdentity()));

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-s02-01"));

        AssertErrorOnce(caller);
        AssertNoSideEffects(harness, conversations, scope);
    }

    // [S02-02] 负控：Begin 传 null subject，或改成从 ChatRequest 读主体 → 本条红。
    [Fact]
    public async Task S02_02_从认证声明取主体名()
    {
        var (hub, harness, conversations, scope, _) = BuildHub("conn-s02-02");
        ArrangeRun(harness, conversations, "conv-s02-02");

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-s02-02"));

        scope.Verify(s => s.Begin("wang", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // [S02-03] 边界：客户端给的 conversationId 只是「我想接着哪个」，归属由仓储判定。
    // 负控：把 Owner() 换成从请求体读，或不传 tenantId/userId → 本条红。
    [Fact]
    public async Task S02_03_按归属取或建会话()
    {
        var (hub, harness, conversations, scope, _) = BuildHub("conn-s02-03");
        ArrangeRun(harness, conversations, "conv-s02-03");

        // 故意传一个别人的会话 id：归属校验若被跳过，这里就会被当成真会话
        await hub.Send(new ChatRequest("attacker-conv", "查一下 PO-1", null, "m-s02-03"));

        conversations.Verify(c => c.GetOrCreateAsync("attacker-conv", "tenant-1", "user-1"), Times.Once);
        // 真正起作用的是仓储返回的那个会话，不是请求里那个
        scope.Verify(s => s.Begin(It.IsAny<string?>(), "conv-s02-03", It.IsAny<string>()), Times.Once);
    }

    // [S02-04] 负控：Begin 少传 conversationId 或 clientMessageId（换成随机值 / 空）→ 本条红。
    [Fact]
    public async Task S02_04_建身份作用域()
    {
        const string messageId = "m-s02-04";
        var (hub, harness, conversations, scope, _) = BuildHub("conn-s02-04");
        ArrangeRun(harness, conversations, "conv-s02-04");

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, messageId));

        scope.Verify(s => s.Begin("wang", "conv-s02-04", messageId), Times.Once);
    }

    // [S02-05] 负控：ActionSubjectProvider.Dispose 不还原 Scoped.Value → 本条红（下一个请求会读到上一个的主体）。
    [Fact]
    public void S02_05_作用域结束后还原()
    {
        var provider = new ActionSubjectProvider(new HttpContextAccessor());

        Assert.Null(provider.CurrentSubject);
        using (provider.Begin("wang", "conv-1", "m-s02-05"))
        {
            Assert.Equal("wang", provider.CurrentSubject);
        }
        Assert.Null(provider.CurrentSubject);
    }

    // [S02-06] 边界：探活本身已经发生，此后零副作用——不是「什么都不做」。
    // 负控：删掉 capabilities.Llm 判空 → 本条红（会真的往下建会话、起 run）。
    [Fact]
    public async Task S02_06_没配模型时提示去配置()
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-s02-06");
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, false, true, true, []));

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-s02-06"));

        AssertErrorOnce(caller);
        harness.Verify(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()), Times.Once);
        AssertNoRun(harness, conversations, scope);
    }

    // [S02-07] 边界：断开是明确终态，不是「不进后续」——已发生的会话 / user 消息 / 作用域必须还在。
    // 负控：把 catch (OperationCanceledException) 改成 return → ReceiveStopped 与部分答复两条断言红。
    [Fact]
    public async Task S02_07_连接断开时给出已停止()
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-s02-07");
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = "conv-s02-07", TenantId = "tenant-1", UserId = "user-1" });
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(StreamThenCancel());

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-s02-07"));

        // 已经发生的副作用必须还在
        conversations.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string?>(), "tenant-1", "user-1"), Times.Once);
        conversations.Verify(c => c.AddMessageAsync("conv-s02-07", "user", "查一下 PO-1"), Times.Once);
        scope.Verify(s => s.Begin(It.IsAny<string?>(), "conv-s02-07", "m-s02-07"), Times.Once);

        // 终态与部分答复：这两项才是「断开」的契约
        var stopped = caller.Invocations.FirstOrDefault(i => (string?)i.Arguments[0] == "ReceiveStopped");
        Assert.NotNull(stopped);
        var payload = (object?[])stopped.Arguments[1];
        Assert.Contains(AgentRunStatus.Cancelled, JsonSerializer.Serialize(payload[0]));
        conversations.Verify(c => c.AddMessageAsync("conv-s02-07", "assistant", "部分"), Times.Once);
        AssertErrorOnce(caller);
    }

    // [S02-08] 负控：删掉 Generations.TryAdd 的判重 → 本条红（第一句的流被调两次）。
    // 边界：这是真发两次的黑盒断言；不用反射往 Generations 里塞值，那样改了实现也不会红。
    [Fact]
    public async Task S02_08_同连接拒绝第二次发送()
    {
        var (hub, harness, conversations, _, caller) = BuildHub("conn-s02-08");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = "conv-s02-08", TenantId = "tenant-1", UserId = "user-1" });
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                entered.TrySetResult();
                return new HarnessCapabilities(true, true, true, true, []);
            });
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns((HarnessRunRequest _, CancellationToken ct) => HoldAsync(gate.Task, ct));

        var first = hub.Send(new ChatRequest(null, "第一句", null, "m-s02-08a"));
        await entered.Task;   // 第一句已经占住连接闸（探活发生在占闸之后）
        await hub.Send(new ChatRequest(null, "第二句", null, "m-s02-08b"));

        harness.Verify(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        AssertErrorOnce(caller);

        gate.TrySetResult();
        await first;
    }

    // ── 装配 / 断言 ────────────────────────────────────────────────

    /// <summary>一个永不自行结束的流：让第一个 run 占住连接闸，好观察第二次被拒。</summary>
    private static async IAsyncEnumerable<HarnessStreamEvent> HoldAsync(
        Task gate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private static async IAsyncEnumerable<HarnessStreamEvent> StreamThenCancel()
    {
        yield return new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "部分");
        await Task.Yield();
        throw new OperationCanceledException("disconnected");
    }

    /// <summary>正常路径：探活就绪、会话已解析、起 run 时用探针停住（只验「走到这一步」）。</summary>
    private static void ArrangeRun(
        Mock<IAgentHarness> harness,
        Mock<IConversationRepository> conversations,
        string conversationId)
    {
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = conversationId, TenantId = "tenant-1", UserId = "user-1" });
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-after-begin"));
    }

    private static void AssertErrorOnce(Mock<ISingleClientProxy> caller)
        => caller.Verify(
            c => c.SendCoreAsync("Error", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);

    private static void AssertNoRun(
        Mock<IAgentHarness> harness,
        Mock<IConversationRepository> conversations,
        Mock<IActionSubjectScope> scope)
    {
        harness.Verify(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        conversations.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        conversations.Verify(c => c.AddMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        scope.Verify(s => s.Begin(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static void AssertNoSideEffects(
        Mock<IAgentHarness> harness,
        Mock<IConversationRepository> conversations,
        Mock<IActionSubjectScope> scope)
    {
        harness.Verify(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()), Times.Never);
        AssertNoRun(harness, conversations, scope);
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
        Mock<ISingleClientProxy> Caller) BuildHub(string connectionId, ClaimsPrincipal? principal = null)
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
        context.SetupGet(c => c.User).Returns(principal ?? Principal());

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

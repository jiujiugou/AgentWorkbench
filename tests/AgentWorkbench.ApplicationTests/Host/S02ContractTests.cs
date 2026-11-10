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
/// S02（Hub：验身份、取会话）的验收装置。**用例名就是判据清单**：
/// <c>dotnet test --filter "FullyQualifiedName~S02" --logger "console;verbosity=normal"</c>
/// 的输出同时给出「这一格有哪些判据」和「每条过没过」，不需要另写实现文档。
///
/// 纪律：每条判据都必须是行为断言，不是源码字符串断言；且必须能变红
/// （负控见每条用例的注释）。恒真的绿等于没验。
/// </summary>
public sealed class S02ContractTests
{
    // [S02-1] 未登录 → 零副作用。负控：删掉 RunAsync 的 IsAuthenticated 判空 → 本条红。
    [Fact]
    public async Task S02_未登录时零副作用()
    {
        var (hub, harness, conversations, scope, caller) =
            BuildHub("conn-s02-1", new ClaimsPrincipal(new ClaimsIdentity()));

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-1"));

        AssertErrorOnce(caller);
        AssertNoSideEffects(harness, conversations, scope);
    }

    // [S02-2] 同连接已有生成 → 零副作用。负控：删掉 Generations.TryAdd 判断 → 本条红。
    [Fact]
    public async Task S02_同连接已有生成时零副作用()
    {
        const string connectionId = "conn-s02-2";
        var field = typeof(AgentHub).GetField("Generations", BindingFlags.NonPublic | BindingFlags.Static)!;
        var generations = (ConcurrentDictionary<string, CancellationTokenSource>)field.GetValue(null)!;
        generations.TryAdd(connectionId, new CancellationTokenSource());
        try
        {
            var (hub, harness, conversations, scope, caller) = BuildHub(connectionId);

            await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-2"));

            AssertErrorOnce(caller);
            AssertNoSideEffects(harness, conversations, scope);
        }
        finally
        {
            generations.TryRemove(connectionId, out _);
        }
    }

    // [S02-3] 模型未配置 → 不建会话、不落消息、不起 run，但**探活已经发生**。
    // 负控：删掉 capabilities.Llm 判空 → 本条红（会真的往下跑）。
    [Fact]
    public async Task S02_模型未配置时探活已发生但不建会话不起run()
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-s02-3");
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, false, true, true, []));

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-3"));

        AssertErrorOnce(caller);
        harness.Verify(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()), Times.Once);
        AssertNoRun(harness, conversations, scope);
    }

    // [S02-4] 断开 = 明确终态 cancelled，**不是「不进后续」**。
    // 反断言「会话与 user 消息仍在」是这条的重点：谁再把断开写成零副作用，本条立刻红。
    // 负控：把 catch (OperationCanceledException) 改成 return → ReceiveStopped / 部分答复两条断言红。
    [Fact]
    public async Task S02_断开是明确终态且会话与消息保留()
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-s02-4");
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = "conv-s02", TenantId = "tenant-1", UserId = "user-1" });
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(StreamThenCancel());

        await hub.Send(new ChatRequest(null, "查一下 PO-1", null, "m-4"));

        // 已经发生的副作用必须还在
        conversations.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string?>(), "tenant-1", "user-1"), Times.Once);
        conversations.Verify(c => c.AddMessageAsync("conv-s02", "user", "查一下 PO-1"), Times.Once);
        scope.Verify(s => s.Begin(It.IsAny<string?>(), "conv-s02", "m-4"), Times.Once);

        // 终态与部分答复：这两项才是「断开」的契约
        var stopped = caller.Invocations.FirstOrDefault(i => (string?)i.Arguments[0] == "ReceiveStopped");
        Assert.NotNull(stopped);
        var payload = (object?[])stopped.Arguments[1];
        Assert.Contains(AgentRunStatus.Cancelled, JsonSerializer.Serialize(payload[0]));
        conversations.Verify(c => c.AddMessageAsync("conv-s02", "assistant", "部分"), Times.Once);
        AssertErrorOnce(caller);
    }

    // [S02-5] subject 与归属只来自认证声明，不来自请求体。
    // 负控：把 Owner() 换成从 ChatRequest 读 → 本条红；Begin 传 null subject → 本条红。
    [Fact]
    public async Task S02_主体名与会话归属来自认证声明()
    {
        var (hub, harness, conversations, scope, _) = BuildHub("conn-s02-5");
        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = "conv-1", TenantId = "tenant-1", UserId = "user-1" });
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-after-begin"));

        // 故意传一个假的 conversationId：归属校验若被跳过，这里就会被当成真会话
        await hub.Send(new ChatRequest("attacker-conv", "查一下 PO-1", null, "m-5"));

        conversations.Verify(c => c.GetOrCreateAsync(
            "attacker-conv", "tenant-1", "user-1"), Times.Once);
        scope.Verify(s => s.Begin("wang", "conv-1", "m-5"), Times.Once);
    }

    // [S02-6] 身份作用域覆盖整段 run，结束后必须还原（并发请求之间不串号）。
    // 负控：SubjectScope.Dispose 不还原 Scoped.Value → 本条红。
    [Fact]
    public void S02_身份作用域覆盖整段run且结束后还原()
    {
        var provider = new ActionSubjectProvider(new HttpContextAccessor());

        Assert.Null(provider.CurrentSubject);
        using (provider.Begin("wang", "conv-1", "m-6"))
        {
            Assert.Equal("wang", provider.CurrentSubject);
        }
        Assert.Null(provider.CurrentSubject);
    }

    // ── 装配 / 断言 ────────────────────────────────────────────────

    private static async IAsyncEnumerable<HarnessStreamEvent> StreamThenCancel()
    {
        yield return new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "部分");
        await Task.Yield();
        throw new OperationCanceledException("disconnected");
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

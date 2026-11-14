using System.Security.Claims;
using System.Text.Json;
using AgentWorkbench.ApplicationTests.ActionGateway;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Ports;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Tools.Builtin;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Controllers;
using AgentWorkbench.Webapi.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// Host 输入身份契约。<b>web 传输模型带来的必答题，只在 Host 内解决，Core 行为零改动。</b>
///
/// 这套用例锁的是「缺身份必须被拒绝」，不是「幂等机制是否工作」——后者由
/// ActionGateway 那组测试覆盖。负控：删掉任一入口的判空，H1 / H3 必须红；
/// 把 InputIdentity.Derive 改回 Guid.NewGuid()，H4 必须红。
/// H11 是守卫：产品后端不得再有 HTTP 起 run 的入口（原第三条入口已移除）。
/// </summary>
public sealed class InputIdentityContractTests
{
    // ── SignalR 入口 ───────────────────────────────────────────────

    /// <summary>
    /// [H1] 缺标识必须零副作用：不建会话、不落 user 消息、不探活、不起 run、不建身份作用域。
    /// 负控：删掉 AgentHub.RunAsync 里的判空 → 本用例红（会真的往下跑）。
    /// </summary>
    [Fact]
    public async Task H1_Hub缺输入标识必须拒绝且零副作用()
    {
        var (hub, harness, conversations, scope, caller) = BuildHub("conn-h1");

        await hub.Send(new ChatRequest(null, "给 S001 下 500 个 M6 螺栓", string.Empty));

        caller.Verify(
            c => c.SendCoreAsync("Error", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Verify(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Verify(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        conversations.Verify(c => c.GetOrCreateAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        conversations.Verify(c => c.AddMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        scope.Verify(s => s.Begin(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// [H2] 带标识时，身份必须真的进入作用域（工具环里 IActionOriginProvider 的唯一来源）。
    /// 让 StreamAsync 在建作用域之后立刻抛，由 Hub 的 catch 收尾——只验「Begin 收到了本次输入的号」。
    /// </summary>
    [Fact]
    public async Task H2_Hub带标识时身份进入作用域()
    {
        var (hub, harness, conversations, scope, _) = BuildHub("conn-h2");

        harness.Setup(h => h.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarnessCapabilities(true, true, true, true, []));
        conversations.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ConversationRecord { Id = "conv-1", TenantId = "tenant-1", UserId = "user-1" });
        harness.Setup(h => h.StreamAsync(It.IsAny<HarnessRunRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("probe-stop-after-begin"));

        await hub.Send(new ChatRequest(null, "查一下 PO-1", "m-h2"));

        scope.Verify(
            s => s.Begin(It.IsAny<string?>(), "conv-1", "m-h2"),
            Times.Once);
    }

    // ── HTTP 入口 ──────────────────────────────────────────────────

    /// <summary>
    /// [H3] 缺标识返回 400，且根本不碰动作网关。
    /// service / store 故意传 null!：判空若被删掉，继续往下走会直接 NullReference 而红。
    /// 负控：删掉 ActionsController.Submit 里的判空 → 本用例红。
    /// </summary>
    [Fact]
    public async Task H3_Controller缺输入标识返回400且零网关调用()
    {
        var controller = new ActionsController(null!, null!, new FakeSubjects("wang"), null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Submit(
            new ActionSubmitBody("test.query", null, " "), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(InputIdentity.MissingCode, JsonSerializer.Serialize(bad.Value));
    }

    /// <summary>
    /// [H4] 同一次输入重放 → 同一个 requestId、回放既有结果、执行体只一次。
    /// 负控：把 InputIdentity.Derive 改回 Guid.NewGuid() → 本用例红。
    /// </summary>
    [Fact]
    public async Task H4_Controller同一次输入重放收敛且只执行一次()
    {
        var (controller, set, _) = BuildController();
        var arguments = JsonDocument.Parse("""{"document_id":"PO-1"}""").RootElement.Clone();

        var first = Dto(await controller.Submit(
            new ActionSubmitBody("test.query", arguments, "m-1"), CancellationToken.None));
        var second = Dto(await controller.Submit(
            new ActionSubmitBody("test.query", arguments, "m-1"), CancellationToken.None));

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal("replayed_from_idempotency", second.Code);
        Assert.Equal(1, set.ExecutorStub.QueryCallCount);
    }

    /// <summary>
    /// [H5] 护栏：换一次输入、参数完全相同，必须是新请求、真的办第二次。
    /// 防止「业务指纹 + 时间窗」式修法误杀用户的真实重复需求（对应 P12）。
    /// </summary>
    [Fact]
    public async Task H5_Controller换一次输入得到新请求号()
    {
        var (controller, set, _) = BuildController();
        var arguments = JsonDocument.Parse("""{"document_id":"PO-1"}""").RootElement.Clone();

        var first = Dto(await controller.Submit(
            new ActionSubmitBody("test.query", arguments, "m-1"), CancellationToken.None));
        var second = Dto(await controller.Submit(
            new ActionSubmitBody("test.query", arguments, "m-2"), CancellationToken.None));

        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(2, set.ExecutorStub.QueryCallCount);
    }

    /// <summary>
    /// [H11] 守卫：产品后端不得再有「用 HTTP 起一次 run」的入口。
    ///
    /// <para>
    /// 曾经有 <c>POST /api/harness/run</c>（原 H9/H10 验的是它的身份判空），已按设计移除。
    /// 理由是它绕过 SignalR Hub，等于同时丢掉四样东西：流式增量、<c>Stop</c>、会话复用，
    /// 以及客户端无法被输入级去重拦住。起 run 的唯一产品入口是 <c>/hubs/agent</c>。
    /// </para>
    /// <para>
    /// 这条断言不验行为，只<b>阻止入口被悄悄加回来</b>：加回一个 HTTP 起 run 的端点，
    /// 上面四个缺口会一起复活，而现有测试一条都不会红——所以必须有一条专门盯着它。
    /// 需要程序化起 run 时：产品路径走 Hub，测试内直接调 <see cref="IAgentHarness.RunAsync"/>。
    /// </para>
    /// </summary>
    [Fact]
    public void H11_产品后端不得再暴露HTTP起run入口()
    {
        Assert.Null(typeof(HarnessController).GetMethod("Run"));
        Assert.DoesNotContain(
            typeof(HarnessController).GetMethods(),
            m => m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Length > 0);
    }

    // ── 守卫本身与契约形状 ────────────────────────────────────────

    /// <summary>[H6] 护栏：键序无关、参数敏感——沿用 Core 既有 Derive 语义，不许被入口改造破坏。</summary>
    [Fact]
    public void H6_参数键序不同同号参数变化新号()
    {
        using var a = JsonDocument.Parse("""{"x":1,"y":2}""");
        using var b = JsonDocument.Parse("""{ "y":2, "x":1 }""");
        using var c = JsonDocument.Parse("""{"x":1,"y":3}""");

        Assert.Equal(
            InputIdentity.Derive("conv-1", "m-1", "test.query", a.RootElement),
            InputIdentity.Derive("conv-1", "m-1", "test.query", b.RootElement));
        Assert.NotEqual(
            InputIdentity.Derive("conv-1", "m-1", "test.query", a.RootElement),
            InputIdentity.Derive("conv-1", "m-1", "test.query", c.RootElement));
    }

    /// <summary>[H7] 契约收紧：提交体不再暴露客户端可自报的最终判重键。</summary>
    [Fact]
    public void H7_提交体不接受客户端自报判重键()
    {
        var names = typeof(ActionSubmitBody).GetConstructors().Single()
            .GetParameters().Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "ActionKey", "Arguments", "ClientMessageId" }, names);
    }

    /// <summary>[H8] Host 的作用域实现确实把身份交给 Core 的只读端口。</summary>
    [Fact]
    public void H8_作用域把身份交给只读端口()
    {
        var provider = new ActionSubjectProvider(new HttpContextAccessor());
        IActionOriginProvider origin = provider;

        using (provider.Begin("wang", "conv-1", "m-1"))
        {
            Assert.Equal("conv-1", origin.ConversationId);
            Assert.Equal("m-1", origin.ClientMessageId);
        }

        Assert.Null(origin.ClientMessageId);
    }

    // ── 装配 ───────────────────────────────────────────────────────

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

    private static (ActionsController Controller, TestActionSet Set, object Store) BuildController()
    {
        var set = new TestActionSet();
        set.Registry.Spec = set.QuerySpec;   // 无副作用、无需审批，能直接走到执行体
        var (service, _, store) = TestHarness.Create(set);
        var controller = new ActionsController(service, store, new FakeSubjects("wang"), null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal() }
            }
        };
        return (controller, set, store);
    }

    private static ActionResultDto Dto(IActionResult result)
        => (ActionResultDto)((ObjectResult)result).Value!;

    /// <summary>HarnessController 的错误走 ProblemDetailsFactory，测试里给一个最小替身。</summary>
    private static ProblemDetailsFactory ProblemFactory()
    {
        var factory = new Mock<ProblemDetailsFactory>();
        factory.Setup(f => f.CreateProblemDetails(
                It.IsAny<HttpContext>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new ProblemDetails());
        return factory.Object;
    }

    private sealed class FakeSubjects(string? subject) : IActionSubjectProvider
    {
        public string? CurrentSubject => subject;
    }
}

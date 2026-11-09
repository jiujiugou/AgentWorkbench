using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.Gitea;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Builtin;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// 方案第 4 步「填单工具」的验收。核心不是「能不能跑通」，而是四条边界：
/// 清单外能力被拒、畸形输出被拒、身份不可仿冒、拔掉模型照样拒绝。
/// 每条都配一个「改坏就变红」的可证伪断言，不用「结构上没有引用」这类恒真判断。
/// </summary>
public sealed class ProposeActionToolTests
{
    private sealed class StubSubjects(string? subject) : IActionSubjectProvider
    {
        public string? Subject { get; set; } = subject;
        public string? CurrentSubject => Subject;
    }

    /// <summary>模拟宿主作用域里的调用来源；ClientMessageId 可改，用于模拟「另一次输入」。</summary>
    private sealed class StubOrigin(string? conversationId, string? clientMessageId) : IActionOriginProvider
    {
        public string? ConversationId { get; set; } = conversationId;
        public string? ClientMessageId { get; set; } = clientMessageId;
    }

    private static (
        ProposeActionTool Tool,
        TestActionSet Set,
        InMemoryTraceStore Trace,
        InMemoryActionRunStore Store,
        StubSubjects Subjects) Create(
        string? subject = "wang",
        bool strict = true,
        IActionOriginProvider? origin = null)
    {
        var set = new TestActionSet();
        set.Registry.MatchKey = strict;
        var (service, trace, store) = TestHarness.Create(set);
        var subjects = new StubSubjects(subject);
        return (new ProposeActionTool(set, subjects, service, origin), set, trace, store, subjects);
    }

    private static JsonElement Body(string reply)
        => JsonDocument.Parse(reply).RootElement.Clone();

    [Fact]
    public void P1_工具只有一个且描述来自注入的能力清单()
    {
        var (tool, set, _, _, _) = Create();
        // 给 schema 一个可辨认的特征串，避免断言 "{}" 这种恒真条件。
        set.Registry.Spec = set.Spec with { InputSchemaJson = """{"title":"capability-alpha"}""" };

        var tools = tool.GetTools();
        Assert.Single(tools);
        Assert.Equal(ProposeActionTool.ToolName, tools[0].Name);
        var description = tools[0].Description;
        Assert.Contains(set.Spec.ActionKey, description);
        Assert.Contains("capability-alpha", description);
        Assert.Contains("不要重试", description);
    }

    [Fact]
    public void P2_换动作域时工具描述自动换成新域能力且底座代码不变()
    {
        var erpSet = new ErpActionSet();
        var labSet = new FakeActionSet();
        var (erpService, _, _) = TestHarness.Create(erpSet);
        var (labService, _, _) = TestHarness.Create(labSet);

        var erpDescription = new ProposeActionTool(erpSet, new StubSubjects("wang"), erpService)
            .GetTools()[0].Description;
        var labDescription = new ProposeActionTool(labSet, new StubSubjects("wang"), labService)
            .GetTools()[0].Description;

        Assert.Contains(ErpActionSpecs.SubmitKey, erpDescription);
        Assert.Contains(LabActionSpecs.SubmitKey, labDescription);
        Assert.DoesNotContain(LabActionSpecs.SubmitKey, erpDescription);
        Assert.DoesNotContain(ErpActionSpecs.SubmitKey, labDescription);
    }

    [Fact]
    public async Task P3_清单外的能力名被拒且执行体零调用()
    {
        // 负控：把 StubSpecRegistry.MatchKey 改回 false（恒返回同一个 spec）→ 本条红。
        var (tool, set, trace, _, _) = Create();
        var reply = Body(await tool.ProposeAsync("not.in.catalog", "{}"));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ActionRejectionCodes.SpecNotFound, reply.GetProperty("code").GetString());
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.True(trace.WriteAttempts > 0, "拒绝也必须留痕，不能静默丢弃");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task P4_畸形或非对象参数被拒且根本不进网关(string malformed)
    {
        var (tool, set, trace, _, _) = Create();
        var reply = Body(await tool.ProposeAsync("test.submit", malformed));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ProposeActionTool.InvalidArgumentsJson, reply.GetProperty("code").GetString());
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Equal(0, trace.WriteAttempts);
    }

    [Fact]
    public async Task P5_模型在参数里塞身份字段也一律按登录态算()
    {
        // 负控：把工具里的 subject 改成从 arguments 读 → 本条红。
        var (tool, set, _, _, _) = Create(subject: "wang");
        var reply = Body(await tool.ProposeAsync(
            "test.submit",
            """{"subject":"boss","role":"owner","approver":"boss"}"""));

        Assert.Equal("pending", reply.GetProperty("status").GetString());
        Assert.NotNull(set.Pdp.LastQuery);
        Assert.Equal("wang", set.Pdp.LastQuery!.Subject);
    }

    [Fact]
    public async Task P6_取不到身份直接拒绝且不进网关()
    {
        // 负控：让工具在身份缺失时回退到一个默认账号 → 本条红。
        var (tool, set, trace, _, _) = Create(subject: null);
        var reply = Body(await tool.ProposeAsync("test.submit", "{}"));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ProposeActionTool.SubjectUnavailable, reply.GetProperty("code").GetString());
        Assert.Equal(0, trace.WriteAttempts);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    [Fact]
    public async Task P7_只回结论码且挂起期间零外部调用()
    {
        var (tool, set, _, store, _) = Create();
        var reply = Body(await tool.ProposeAsync("test.submit", "{}"));

        Assert.Equal("pending", reply.GetProperty("status").GetString());
        Assert.Equal(ActionRejectionCodes.ApprovalRequired, reply.GetProperty("code").GetString());
        // 只回 status / requestId / code：业务数据与外部单号不进模型上下文。
        Assert.Equal(3, reply.EnumerateObject().Count());
        Assert.False(reply.TryGetProperty("data", out _));
        Assert.False(reply.TryGetProperty("externalId", out _));
        Assert.Equal(0, set.ExecutorStub.WriteCallCount);
        Assert.Equal(1, store.SuspendCount);
    }

    [Fact]
    public async Task P8_拔掉模型手工递同一张单照样被拒()
    {
        // 判据③：安全不靠模型自觉。工具链与手工链面对同一个清单外请求，结论一致。
        var (_, set, _, _, _) = Create();
        var (service, _, _) = TestHarness.Create(set);
        var result = await service.SubmitDeferredAsync(
            TestRequests.Create(requestId: "manual-1", actionKey: "not.in.catalog"),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.SpecNotFound, result.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    [Fact]
    public void P10_第三域接入后清单变为业务对象粒度且不依赖连接参数()
    {
        // 「不配 token 也能让模型看到能力清单」：Unconfigured 下动作全部 fail-closed 且零 HTTP，
        // 但 Specs.ListAsync 照常返回。这正是 AC-01 要证明的「换域只换装配，底座不改」。
        var giteaSet = new GiteaActionSet(GiteaOptions.Unconfigured);
        var (service, _, _) = TestHarness.Create(giteaSet);

        var description = new ProposeActionTool(giteaSet, new StubSubjects("wang"), service)
            .GetTools()[0].Description;

        // 业务对象粒度：issue.create / issue.close 而不是 erp.submit 这类原语名。
        Assert.Contains(GiteaActionSpecs.IssueCreateKey, description);
        Assert.Contains(GiteaActionSpecs.IssueCloseKey, description);
        Assert.DoesNotContain(ErpActionSpecs.SubmitKey, description);
        Assert.DoesNotContain("erp.", description);
        // 写能力必须标出需审批，否则模型会把 pending 当失败去重试。
        Assert.Contains("需人工审批：True", description);
    }

    [Fact]
    public void P9_工具委托与公开方法指向同一条路径()
    {
        // 防止「测试调 A、工具调 B」的假绿：GetTools 返回的必须是一个可调用的 AIFunction。
        var (tool, _, _, _, _) = Create();
        var function = Assert.IsAssignableFrom<AIFunction>(tool.GetTools()[0]);
        Assert.Equal(ProposeActionTool.ToolName, function.Name);
        Assert.NotNull(function.JsonSchema);
        Assert.Contains("actionKey", function.JsonSchema.ToString());
        Assert.Contains("argumentsJson", function.JsonSchema.ToString());
    }

    // ---------------- 去重键：请求号由「哪一次输入 + 哪个能力 + 什么参数」派生 ----------------
    //
    // 这一组守的是一件事：requestId 不能随机。随机的号会让「同一件事被触发两次」永远
    // 命中不了去重表，于是第⑥步之后所有的幂等承诺在模型重试面前全部失效。

    [Fact]
    public async Task P11_同一次输入的重复触发收敛到同一请求号且第二次走幂等回放()
    {
        var origin = new StubOrigin("conv-1", "msg-1");
        var (tool, set, _, _, _) = Create(origin: origin);
        // 换成免审批的只读规格：两次调用都真的走到执行体，才能验「只执行了一次」。
        set.Registry.Spec = set.QuerySpec;

        var first = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));
        var second = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));

        Assert.Equal(
            first.GetProperty("requestId").GetString(),
            second.GetProperty("requestId").GetString());
        Assert.Equal("completed", first.GetProperty("code").GetString());
        // 第二次不是重新执行，是回放既有结果——这是「同一件事只做一次」的可证伪断言。
        Assert.Equal(
            ActionSuccessCodes.ReplayedFromIdempotency,
            second.GetProperty("code").GetString());
        Assert.Equal(1, set.ExecutorStub.QueryCallCount);
    }

    [Fact]
    public async Task P12_换一次输入得到新请求号并各执行一次()
    {
        var origin = new StubOrigin("conv-1", "msg-1");
        var (tool, set, _, _, _) = Create(origin: origin);
        set.Registry.Spec = set.QuerySpec;

        var first = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));
        origin.ClientMessageId = "msg-2";   // 用户又说了同一句话：这是新输入，该办第二次
        var second = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));

        Assert.NotEqual(
            first.GetProperty("requestId").GetString(),
            second.GetProperty("requestId").GetString());
        Assert.Equal("completed", second.GetProperty("code").GetString());
        Assert.Equal(2, set.ExecutorStub.QueryCallCount);
    }

    [Fact]
    public async Task P13_同一次输入但参数变了就是新请求()
    {
        // 模型超时后改参数重试，是新的意图，必须放行；否则会把「修正」当成「重复」吞掉。
        var origin = new StubOrigin("conv-1", "msg-1");
        var (tool, set, _, _, _) = Create(origin: origin);
        set.Registry.Spec = set.QuerySpec;

        var first = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));
        var second = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-2"}"""));

        Assert.NotEqual(
            first.GetProperty("requestId").GetString(),
            second.GetProperty("requestId").GetString());
        Assert.Equal(2, set.ExecutorStub.QueryCallCount);
    }

    [Fact]
    public async Task P14_没有来源标识时退化为一次性请求号()
    {
        // 事实锁定：Core 侧的 Derive 仍保留「没有来源标识就退化成一次性号」这条回退。
        // 这是 Core 内部行为，不是 web 生产路径——Host 的三条入口都要求 clientMessageId 必填，
        // 从那里进来走不到这个分支（唯一例外是 Workflow 的 AgentStepExecutor，它绕过 Host）。
        // 见 src/Host/AgentWorkbench.Webapi/README.md「输入身份契约」。
        // 断言保留：绝不能退化成「所有请求共用一个号」——那会把不重复执行变成不执行。
        var (tool, set, _, _, _) = Create(origin: null);
        set.Registry.Spec = set.QuerySpec;

        var first = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));
        var second = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1"}"""));

        Assert.NotEqual(
            first.GetProperty("requestId").GetString(),
            second.GetProperty("requestId").GetString());
        Assert.Equal(2, set.ExecutorStub.QueryCallCount);
    }

    [Fact]
    public async Task P15_参数键序不同仍算同一次请求()
    {
        // 模型每次序列化的键序/空格可能不同。若原文直接参与算号，
        // 「原样重试」会因为格式差异被当成新请求，去重当场失效。
        var origin = new StubOrigin("conv-1", "msg-1");
        var (tool, set, _, _, _) = Create(origin: origin);
        set.Registry.Spec = set.QuerySpec;

        var first = Body(await tool.ProposeAsync("test.query", """{"document_id":"PO-1","amount":100}"""));
        var second = Body(await tool.ProposeAsync("test.query", """{ "amount":100 , "document_id":"PO-1" }"""));

        Assert.Equal(
            first.GetProperty("requestId").GetString(),
            second.GetProperty("requestId").GetString());
        Assert.Equal(
            ActionSuccessCodes.ReplayedFromIdempotency,
            second.GetProperty("code").GetString());
        Assert.Equal(1, set.ExecutorStub.QueryCallCount);
    }

    [Fact]
    public void P16_派生规则对语义相同写法给出相同号语义不同给出不同号()
    {
        using var docA = JsonDocument.Parse("""{"b":1,"a":"中文"}""");
        using var docB = JsonDocument.Parse("""{"a":"\u4e2d\u6587","b":1}""");
        using var docC = JsonDocument.Parse("""{"a":"中文","b":2}""");

        var a = ActionRequestId.Derive("c", "m", "k", docA.RootElement.Clone());
        var b = ActionRequestId.Derive("c", "m", "k", docB.RootElement.Clone());
        var c = ActionRequestId.Derive("c", "m", "k", docC.RootElement.Clone());

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(ActionRequestId.Length, a.Length);
        Assert.Equal(a.ToLowerInvariant(), a);
    }
}

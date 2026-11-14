using System.Text.Json;
using AgentWorkbench.ApplicationTests.ActionGateway;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.Gitea;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Builtin;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S05（<c>propose_action</c> 填成一张单子）的验收装置。<b>一个方法 = 一个步骤功能</b>：
/// 方法名说的是「这一步做什么」，绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>边界</b>：起于模型给出的 actionKey + argumentsJson，止于 <c>ActionRequest</c> 进网关。
/// 四栏覆盖：①请求号不稳定 → 11–16；②参数非法 / 底座试图理解参数 → 04、05、18、21；
/// ③主体取自请求体 → 05、06、17、20；「交给下一格什么」→ 17、18；
/// 「不许留下什么」→ 04、06、19、20、22。
/// <b>不归本格</b>：四道闸的判定（S06）；<c>requestId</c> 的具体算法。11–16 只验
/// 「稳定、可由主体+会话+编号+动作+参数复算」这一条契约，不锁定哈希算法或截断长度。
///
/// <b>迁移说明</b>：01–16 原是 <c>ProposeActionToolTests</c> 的 P1–P16，本轮收敛成 S05 编号，
/// 断言一条未改（仅把 <c>Assert.NotNull</c> 对值类型 <c>JsonElement</c> 的恒真断言换成
/// 有内容的 <c>ValueKind</c> 断言）。此前门禁按 <c>S05</c> 前缀计数认不到它们，本格显示 0 条 BLOCKED
/// ——<b>不是没写，是没登记在同一套编号下</b>。17–22 是本轮补的判据。
///
/// <b>层 × 方向</b>：01–16 是 Core（<c>Tools.Builtin</c>）+ Host（提供输入身份作用域）；
/// 17–22 用 <c>CapturingSet</c> 在第 1 道闸前直接捕获 <c>ActionRequest</c>，
/// 把「不进网关」「参数原样」从间接证据（trace 写次数）升级成直接证据。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S05ContractTests
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
    public void S05_01_工具只有一个且描述来自注入的能力清单()
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
    public void S05_02_换动作域时工具描述自动换成新域能力且底座代码不变()
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
    public async Task S05_03_清单外的能力名被拒且执行体零调用()
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
    public async Task S05_04_畸形或非对象参数被拒且根本不进网关(string malformed)
    {
        var (tool, set, trace, _, _) = Create();
        var reply = Body(await tool.ProposeAsync("test.submit", malformed));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ProposeActionTool.InvalidArgumentsJson, reply.GetProperty("code").GetString());
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Equal(0, trace.WriteAttempts);
    }

    [Fact]
    public async Task S05_05_模型在参数里塞身份字段也一律按登录态算()
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
    public async Task S05_06_取不到身份直接拒绝且不进网关()
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
    public async Task S05_07_只回结论码且挂起期间零外部调用()
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
    public async Task S05_08_拔掉模型手工递同一张单照样被拒()
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
    public void S05_09_工具委托与公开方法指向同一条路径()
    {
        // 防止「测试调 A、工具调 B」的假绿：GetTools 返回的必须是一个可调用的 AIFunction。
        var (tool, _, _, _, _) = Create();
        var function = Assert.IsAssignableFrom<AIFunction>(tool.GetTools()[0]);
        Assert.Equal(ProposeActionTool.ToolName, function.Name);
        Assert.Equal(JsonValueKind.Object, function.JsonSchema.ValueKind);
        Assert.Contains("actionKey", function.JsonSchema.ToString());
        Assert.Contains("argumentsJson", function.JsonSchema.ToString());
    }

    [Fact]
    public void S05_10_第三域接入后清单变为业务对象粒度且不依赖连接参数()
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

    // ---------------- 去重键：请求号由「哪一次输入 + 哪个能力 + 什么参数」派生 ----------------
    //
    // 这一组守的是一件事：requestId 不能随机。随机的号会让「同一件事被触发两次」永远
    // 命中不了去重表，于是第⑥步之后所有的幂等承诺在模型重试面前全部失效。

    [Fact]
    public async Task S05_11_同一次输入的重复触发收敛到同一请求号且第二次走幂等回放()
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
    public async Task S05_12_换一次输入得到新请求号并各执行一次()
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
    public async Task S05_13_同一次输入但参数变了就是新请求()
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
    public async Task S05_14_没有来源标识时退化为一次性请求号()
    {
        // 事实锁定：Core 侧的 Derive 仍保留「没有来源标识就退化成一次性号」这条回退。
        // 这是 Core 内部行为，不是 web 生产路径——Host 的入口都要求 clientMessageId 必填，
        // 从那里进来走不到这个分支（目前没有绕过 Host 的调用方）。
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
    public async Task S05_15_参数键序不同仍算同一次请求()
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
    public void S05_16_派生规则对语义相同写法给出相同号语义不同给出不同号()
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

    // ── 17–22：文档四栏里此前只有间接证据的部分 ──────────────────────
    // 01–16 靠 trace 写次数、执行体调用次数推断「有没有进网关」；
    // 这里改成在第 1 道闸前直接捕获 ActionRequest，把推断换成事实。

    // [S05-17] 「交给下一格什么」：subject 与 requestId 都齐全，且回包与请求用同一个号。
    // 负控：工具把 subject 留空 / 回包另编一个 requestId / 不填 IdempotencyKey → 本条红。
    [Fact]
    public async Task S05_17_交给下一格的请求主体与请求号都齐全()
    {
        var (tool, _, validator, _, _) = CreateCapturing(origin: new StubOrigin("conv-1", "msg-1"));

        var reply = Body(await tool.ProposeAsync("test.submit", """{"document_id":"PO-1"}"""));

        Assert.Equal("pending", reply.GetProperty("status").GetString());
        var request = validator.Last;
        Assert.NotNull(request);
        Assert.Equal("wang", request!.Subject);
        Assert.Equal("test.submit", request.ActionKey);
        Assert.False(string.IsNullOrWhiteSpace(request.RequestId));
        Assert.Equal(reply.GetProperty("requestId").GetString(), request.RequestId);
        Assert.Equal(request.RequestId, request.IdempotencyKey);
    }

    // [S05-18] 参数对底座不透明：含「超额金额 + 嵌套 + 数组 + 中文 + 转义引号」的任意业务对象
    // 必须原样透传，底座既不解析字段、也不因内容拒绝（金额档位是域的判据，归 S06）。
    // 负控：把参数反序列化成业务类型 / 丢字段 / 在工具里按金额提前拒绝 → 本条红。
    [Fact]
    public async Task S05_18_参数原样透传且底座不按内容拒绝()
    {
        const string payload = """{"document_id":"PO-1","amount":999999,"lines":[{"sku":"M6","qty":500}],"备注":"中文 \" 引号"}""";
        var (tool, _, validator, _, _) = CreateCapturing(origin: new StubOrigin("conv-1", "msg-1"));

        var reply = Body(await tool.ProposeAsync("test.submit", payload));

        Assert.Equal("pending", reply.GetProperty("status").GetString());
        var captured = validator.Last;
        Assert.NotNull(captured);
        Assert.Equal(payload, captured!.Arguments.GetRawText());
        Assert.Equal(999999, captured.Arguments.GetProperty("amount").GetInt32());
        Assert.Equal(1, captured.Arguments.GetProperty("lines").GetArrayLength());
    }

    // [S05-19] 直接证据：畸形参数在第 1 道闸之前就被挡下，网关一次都没被调用。
    // 负控：把工具里的 JSON 校验挪到 SubmitDeferredAsync 之后 → validator.Calls 变 1，本条红。
    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task S05_19_畸形参数根本不进网关(string malformed)
    {
        var (tool, set, validator, trace, _) = CreateCapturing();

        var reply = Body(await tool.ProposeAsync("test.submit", malformed));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ProposeActionTool.InvalidArgumentsJson, reply.GetProperty("code").GetString());
        Assert.Equal(0, validator.Calls);
        Assert.Equal(0, trace.WriteAttempts);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    // [S05-20] 身份取不到同样不进网关（与 06 的区别：这里是直接捕获，不靠 trace 推断）。
    // 负控：让工具在身份缺失时回退到默认账号 → 本条红。
    [Fact]
    public async Task S05_20_无身份根本不进网关()
    {
        var (tool, set, validator, trace, _) = CreateCapturing(subject: null);

        var reply = Body(await tool.ProposeAsync("test.submit", "{}"));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ProposeActionTool.SubjectUnavailable, reply.GetProperty("code").GetString());
        Assert.Equal(0, validator.Calls);
        Assert.Equal(0, trace.WriteAttempts);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    // [S05-21] 空参数串按空对象处理，而不是当成非法 JSON 拒绝。
    // 负控：删掉 ProposeAsync 里 `argumentsJson 空白 ? "{}" : argumentsJson` 的回退 → 本条红。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task S05_21_空参数串按空对象处理(string blank)
    {
        var (tool, _, validator, _, _) = CreateCapturing();

        var reply = Body(await tool.ProposeAsync("test.submit", blank));

        Assert.Equal("pending", reply.GetProperty("status").GetString());
        Assert.NotNull(validator.Last);
        Assert.Equal(JsonValueKind.Object, validator.Last!.Arguments.ValueKind);
        Assert.Empty(validator.Last.Arguments.EnumerateObject());
    }

    // [S05-22] 空能力名被拒，且回包不带 requestId —— 被拒的请求没有编号，
    // 模型拿不到一个可以去查、去重试的凭据。
    // 负控：删掉工具里的 actionKey 判空 → 本条红（会走到网关拿 SpecNotFound）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task S05_22_空能力名被拒且回包不带请求号(string blank)
    {
        var (tool, _, validator, trace, _) = CreateCapturing();

        var reply = Body(await tool.ProposeAsync(blank, "{}"));

        Assert.Equal("rejected", reply.GetProperty("status").GetString());
        Assert.Equal(ProposeActionTool.ActionKeyRequired, reply.GetProperty("code").GetString());
        Assert.Equal(string.Empty, reply.GetProperty("requestId").GetString());
        Assert.Equal(0, validator.Calls);
        Assert.Equal(0, trace.WriteAttempts);
    }

    // ── 捕获装置：只替换第 1 道闸，其余五件仍用 TestActionSet  ─────────

    private sealed class CapturingValidator : ISchemaValidator
    {
        public int Calls { get; private set; }
        public ActionRequest? Last { get; private set; }

        public ValueTask<SchemaDecision> ValidateAsync(
            ActionSpec spec, ActionRequest request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return ValueTask.FromResult(new SchemaDecision(true, "captured"));
        }
    }

    /// <summary>转发 TestActionSet 的全部注入口，唯独 Schema 换成能看见请求的那个。</summary>
    private sealed class CapturingSet(TestActionSet inner) : IActionSet
    {
        public CapturingValidator Validator { get; } = new();
        public string Name => inner.Name;
        public IActionSpecRegistry Specs => inner.Specs;
        public ISchemaValidator Schema => Validator;
        public IPermissionQuery Permissions => inner.Permissions;
        public IBusinessRule Rules => inner.Rules;
        public IActionExecutor Executor => inner.Executor;
        public IApprovalGate Approvals => inner.Approvals;
        public ActionSpec QuerySpec => inner.QuerySpec;
    }

    /// <summary>走真实 ProposeActionTool → 真实 ActionRunService，只把第 1 道闸换成捕获器。</summary>
    private static (
        ProposeActionTool Tool,
        TestActionSet Set,
        CapturingValidator Validator,
        InMemoryTraceStore Trace,
        InMemoryActionRunStore Store) CreateCapturing(
        string? subject = "wang",
        IActionOriginProvider? origin = null)
    {
        var set = new TestActionSet();
        set.Registry.MatchKey = true;
        var wrapped = new CapturingSet(set);
        var (service, trace, store) = TestHarness.Create(wrapped);
        var tool = new ProposeActionTool(wrapped, new StubSubjects(subject), service, origin);
        return (tool, set, wrapped.Validator, trace, store);
    }
}

using System.Security.Claims;
using AgentWorkbench.ApplicationTests.ActionGateway;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Ports;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S06（四道闸门）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>边界</b>：起于 <c>ActionRequest</c> 进网关，止于 <c>Validated</c> 交给 S07。
/// 四道按序 = Schema → Permission → BusinessRule → Audit 落痕。四栏覆盖：
/// ①顺序被换 → 02、08、09、10；②某道没落痕 / 落痕没依据 → 01–03、08、12；
/// ③审计写失败仍放行 → 04；「不许留下什么」→ 01–04、10、13。
/// <b>不归本格</b>：规则与权限的<b>内容</b>（域注入）、<c>RiskLevel</c> 是否参与决策、
/// 是否需要审批（S07）。四道里第 4 道是「审计落痕本身」，不是 <c>IApprovalGate</c>。
///
/// <b>迁移说明</b>：01–07 原是 <c>GatewayFourChecksTests</c> 的 D1–D6 + NoModel，
/// 本轮收敛成 S06 编号，断言一条未改。此前门禁按 <c>S06</c> 前缀计数认不到它们，
/// 本格显示 0 条 BLOCKED——<b>不是没写，是没登记在同一套编号下</b>（与 S05 同因）。
/// 08–13 是本轮补的判据。
///
/// <b>层 × 方向</b>：08–12 是 Core（<c>FailClosedActionGateway</c> / <c>ActionRunService</c>）；
/// 13 是 Host 的 <c>Rejected → HTTP 422</c> 映射——文档把它写进本格「不许留下什么」，
/// 所以一并验，但断言只落在状态码与零执行上，不替 Host 验别的。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S06ContractTests
{
    private static readonly string[] GatewayStages =
        [AuditStages.Schema, AuditStages.Permission, AuditStages.BusinessRule, AuditStages.Audit];
    private static readonly string[] GatewayOutcomes = ["allow", "allow", "allow", "written"];

    [Fact]
    public async Task S06_01_Schema拒绝_执行体零调用且留痕()
    {
        // 负控：把 FailClosedActionGateway 里 `if (!schema.Valid) return ...` 改成恒 false
        //       → 本条的 Rejected/SchemaInvalid 断言会红，且后续测试会看到执行体被调用。
        var set = new TestActionSet();
        set.Validator.Valid = false;
        var (service, trace, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.SchemaInvalid, result.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Contains(trace.Events, e => e.Stage == AuditStages.Schema && e.Outcome == "deny");
    }

    [Fact]
    public async Task S06_02_权限拒绝_执行体零调用且后续两道不再执行()
    {
        // 负控：把 `if (!permission.Allow) return ...` 去掉 → 本条红，且 D2 之后会走到执行体。
        var set = new TestActionSet();
        set.Pdp.Allow = false;
        var (service, trace, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.PermissionDenied, result.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Equal(0, set.Rule.CallCount); // 按序：第 2 道不过，第 3 道不得执行
        Assert.Contains(trace.Events, e => e.Stage == AuditStages.Permission && e.Outcome == "deny");
    }

    [Fact]
    public async Task S06_03_业务规则拒绝_执行体零调用()
    {
        // 负控：把 `if (!rule.Allow) return ...` 去掉 → 本条红。
        var set = new TestActionSet();
        set.Rule.Allow = false;
        var (service, trace, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.BusinessRuleDenied, result.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Contains(trace.Events, e => e.Stage == AuditStages.BusinessRule && e.Outcome == "deny");
    }

    [Fact]
    public async Task S06_04_审计写不进去_必须拒绝不得静默放行()
    {
        // 负控：把网关里 `if (!await TraceAsync(...)) return Reject(...)` 的返回值改成忽略
        //       → 本条红（会放行到执行体）。
        var set = new TestActionSet();
        var (service, trace, _) = TestHarness.Create(set);
        trace.Available = false;

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.AuditWriteFailed, result.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    [Fact]
    public async Task S06_05_四道全通过_才调用执行体一次()
    {
        // 负控：把 ActionRunService 里 `if (gate.Status != ActionStatuses.Validated) return gate;`
        //       改成恒不返回 → 拒绝路径也会执行，D1–D4 全红。
        var set = new TestActionSet();
        var (service, trace, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal("EXT-0001", result.ExternalId);
        Assert.Equal(1, set.ExecutorStub.WriteCallCount);
        Assert.Equal(1, set.Validator.CallCount);
        Assert.Equal(1, set.Pdp.CallCount);
        Assert.Equal(1, set.Rule.CallCount);
        Assert.Contains(trace.Events, e => e.Stage == AuditStages.Audit && e.Outcome == "written");
    }

    [Fact]
    public async Task S06_06_零配置默认全拒绝_不提供任何放行默认()
    {
        // 负控：把 DenyAllSchemaValidator 的 ValidateAsync 改成返回 true → 本条红。
        var trace = new InMemoryTraceStore();
        var gateway = FailClosedDefaults.Create(trace);

        var result = await gateway.SubmitAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.SchemaInvalid, result.Code);
        Assert.NotEmpty(trace.Events);
    }

    [Fact]
    public async Task S06_07_拔掉模型畸形参数照样拒绝且留痕()
    {
        // X2 装置：拿掉模型（本条完全不构造任何 IChatClient / Agent），或让模型吐畸形 JSON
        //（这里用 JSON 字符串冒充结构化 Arguments），系统仍然拒绝且留痕。
        // 负控：把 DenyAll* 任一改成放行 → 本条红。
        var trace = new InMemoryTraceStore();
        var gateway = FailClosedDefaults.Create(trace);
        var malformed = TestRequests.Create(argumentsJson: "\"这不是结构化参数\"");

        var result = await gateway.SubmitAsync(malformed, CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Contains(trace.Events, e => e.Outcome == "deny" && e.Arguments.Contains("这不是结构化参数"));
        Assert.All(trace.Events, e => Assert.Equal(malformed.RequestId, e.RequestId));
    }

    // ── 08–13：文档四栏里此前只验了「被拒」，没验「通过时交出去的是什么」 ──────

    // [S06-08] 「交给下一格什么」的前半：四道都成功落痕，且严格按序。
    // 直接打网关本体（不经 ActionRunService），轨迹里就只有这四笔，顺序断言不会被收口轨迹干扰。
    // 负控：删掉任一 TraceAsync 调用、或把两道对调 → 本条红。
    [Fact]
    public async Task S06_08_四道全通过时每道都按序落痕()
    {
        var trace = new InMemoryTraceStore();
        var gateway = FailClosedActionGateway.FromActionSet(new TestActionSet(), trace);

        var result = await gateway.SubmitAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Validated, result.Status);
        Assert.Equal(GatewayStages, trace.Events.Select(e => e.Stage).ToArray());
        Assert.Equal(GatewayOutcomes, trace.Events.Select(e => e.Outcome).ToArray());
    }

    // [S06-09] 顺序铁律：权限必须在业务规则之前。02 只验了「权限拒绝时业务规则不跑」，
    // 反方向没锁——把 PDP 挪到 Rule 后面，02 照样绿，但「越权者先被业务规则放进来」已经发生。
    // 负控：把 FailClosedActionGateway 里 PDP 与 Rule 两段对调 → 本条红。
    [Fact]
    public async Task S06_09_权限判定先于业务规则()
    {
        var set = new TestActionSet();
        set.Rule.Allow = false;
        var (service, trace, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.BusinessRuleDenied, result.Code);
        Assert.Equal(1, set.Pdp.CallCount);   // 业务规则判拒之前，权限已经问过
        var stages = trace.Events.Select(e => e.Stage).ToList();
        Assert.True(
            stages.IndexOf(AuditStages.Permission) < stages.IndexOf(AuditStages.BusinessRule),
            "权限阶段必须排在业务规则阶段之前");
    }

    // [S06-10] 按序短路：任一道 deny，它后面的判定、审批、执行体一次都不该跑。
    // 01–03 各自只验了执行体零调用，没有把「后续判定」也钉住。
    // 负控：删掉任一道的提前 return → 对应行红。
    [Theory]
    [InlineData("schema")]
    [InlineData("permission")]
    [InlineData("business_rule")]
    public async Task S06_10_任一道拒绝后续判定审批与执行体都零调用(string deniedStage)
    {
        var set = new TestActionSet();
        switch (deniedStage)
        {
            case "schema": set.Validator.Valid = false; break;
            case "permission": set.Pdp.Allow = false; break;
            case "business_rule": set.Rule.Allow = false; break;
        }
        var (service, _, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Equal(0, set.Approval.CallCount);
        if (deniedStage == "schema")
        {
            Assert.Equal(0, set.Pdp.CallCount);
            Assert.Equal(0, set.Rule.CallCount);
        }
        if (deniedStage == "permission")
            Assert.Equal(0, set.Rule.CallCount);
    }

    // [S06-11] 「交给下一格什么」的后半：网关交出的是 Validated（非终态），
    // 不带外部单号，且网关自己绝不调用执行体——执行权在 S07/S08，不在这一格。
    // 负控：让网关直接调 IActionExecutor，或把 Validated 改成 Succeeded → 本条红。
    [Fact]
    public async Task S06_11_网关交出的是Validated且自己不执行()
    {
        var set = new TestActionSet();
        var trace = new InMemoryTraceStore();
        var gateway = FailClosedActionGateway.FromActionSet(set, trace);

        var result = await gateway.SubmitAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Validated, result.Status);
        Assert.Equal("validated", result.Code);
        Assert.Null(result.ExternalId);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    // [S06-12] 「事后说不清依据」：落痕不只要有 Stage/Outcome，还得带那道判定的理由。
    // 08 验的是 allow 路径齐全，这条验 deny 路径的 Result 不是空壳。
    // 负控：把 TraceAsync 里的 rule.Reason 换成 null → 本条红。
    [Fact]
    public async Task S06_12_拒绝的那道落痕带得出判定依据()
    {
        var set = new TestActionSet();
        set.Rule.Allow = false;
        var (service, trace, _) = TestHarness.Create(set);

        var result = await service.RunAsync(TestRequests.Create(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        var denied = trace.Events.Where(e => e.Stage == AuditStages.BusinessRule).ToList();
        Assert.Single(denied);
        Assert.Equal("deny", denied[0].Outcome);
        Assert.Contains("stub deny", denied[0].Result!);
    }

    // [S06-13] 文档「不许留下什么」写的是 `Rejected（HTTP 422）`。
    // 状态码由 Host 映射，但判据写在本格，就一并验；只断言 422 与零执行，不替 Host 验别的。
    // 负控：把 ActionsController 里 `Rejected => UnprocessableEntity(...)` 改成 Ok(...) → 本条红。
    [Fact]
    public async Task S06_13_被拒映射成422且执行体零调用()
    {
        var set = new TestActionSet();
        set.Validator.Valid = false;
        var (service, _, store) = TestHarness.Create(set);
        var controller = new ActionsController(service, store, new StubSubjects("wang"), null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal() }
            }
        };

        var result = await controller.Submit(
            new ActionSubmitBody("test.submit", null, "m-s06-13"), CancellationToken.None);

        var rejected = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(422, rejected.StatusCode);
        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    // ── Host 侧最小装置（13 用） ─────────────────────────────────────

    private sealed class StubSubjects(string? subject) : IActionSubjectProvider
    {
        public string? CurrentSubject => subject;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "user-1"),
        new Claim(CurrentUser.TenantIdClaim, "tenant-1"),
        new Claim(ClaimTypes.Name, "wang"),
    ], "test"));
}

using AgentWorkbench.ActionGateway;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// M1 退出条件之一：四道校验各有单测（设计 §4 / §8.1）。
/// 每条都断言「被拒时执行体零调用」，负控写在各自注释里 —— 改坏对应那道判定即可看到变红。
/// </summary>
public sealed class GatewayFourChecksTests
{
    [Fact]
    public async Task D1_Schema拒绝_执行体零调用且留痕()
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
    public async Task D2_权限拒绝_执行体零调用且后续两道不再执行()
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
    public async Task D3_业务规则拒绝_执行体零调用()
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
    public async Task D4_审计写不进去_必须拒绝不得静默放行()
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
    public async Task D5_四道全通过_才调用执行体一次()
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
    public async Task D6_零配置默认全拒绝_不提供任何放行默认()
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
    public async Task NoModel_MalformedJson_StillRejects_AndLeavesTrace()
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
}

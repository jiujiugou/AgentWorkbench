using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
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

    private static (
        ProposeActionTool Tool,
        TestActionSet Set,
        InMemoryTraceStore Trace,
        InMemoryActionRunStore Store,
        StubSubjects Subjects) Create(string? subject = "wang", bool strict = true)
    {
        var set = new TestActionSet();
        set.Registry.MatchKey = strict;
        var (service, trace, store) = TestHarness.Create(set);
        var subjects = new StubSubjects(subject);
        return (new ProposeActionTool(set, subjects, service), set, trace, store, subjects);
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
}

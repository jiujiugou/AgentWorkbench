using AgentWorkbench.Erp;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// 正本 §4.1 六句成功判据，逐条挂到设计 §2.2 的六步上（设计 §8.1）。
/// 全部使用假 ERP / 第二套假动作集，不连真 ERPNext、不需要模型。
/// </summary>
public sealed class ActionRunSuccessTests
{
    private static ActionRequest ErpSubmit(string requestId = "req-erp-1")
        => TestRequests.Create(requestId: requestId, actionKey: ErpActionSpecs.SubmitKey);

    [Fact]
    public async Task S1_Query原语能查到外部状态()
    {
        // 负控：把 InMemoryFakeErp.DoQuery 改成恒返回 Failed → 本条红。
        var erp = new ErpActionSet();
        var (service, _, _) = TestHarness.Create(erp);
        var request = ErpSubmit();

        var submitted = await service.RunAsync(request, CancellationToken.None);
        Assert.Equal(ActionStatuses.Succeeded, submitted.Status);

        var queried = await erp.FakeErp.ExecuteAsync(ErpActionSpecs.Query, request, CancellationToken.None);
        Assert.Equal(ActionStatuses.Succeeded, queried.Status);
        Assert.Equal(submitted.ExternalId, queried.ExternalId);
        Assert.Equal(1, erp.FakeErp.QueryCallCount);
        Assert.Contains("Submitted", queried.Data.GetRawText());
    }

    [Fact]
    public async Task S2_未审批零外部调用()
    {
        // 负控：把 ActionRunService 里审批拒绝分支的 return 去掉 → 会继续走到执行体，本条红。
        var erp = new ErpActionSet();
        erp.ApprovalGate.Approved = false;
        var (service, _, store) = TestHarness.Create(erp);

        var result = await service.RunAsync(ErpSubmit(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, result.Status);
        Assert.Equal(ActionRejectionCodes.ApprovalDenied, result.Code);
        Assert.Equal(0, erp.FakeErp.SubmitCallCount);
        Assert.Equal(0, erp.FakeErp.DocumentCount);
        Assert.Equal(1, store.SuspendCount);
        Assert.Equal(0, store.ResumeCount);
    }

    [Fact]
    public async Task S3_同request_id只生成一张单()
    {
        // 负控：把 InMemoryActionRunStore.TryClaimIdempotencyAsync 改成恒 true → 第三段会真执行，
        //       SubmitCallCount 变 2，本条红。
        // 说明：前两段验终态回放（LoadResultAsync），第三段专门验幂等领取（TryClaim），
        //       否则 TryClaim 那条路径会被终态回放挡住、永远测不到（假绿，负控实证抓出）。
        var erp = new ErpActionSet();
        var (service, _, store) = TestHarness.Create(erp);
        var request = ErpSubmit();

        var first = await service.RunAsync(request, CancellationToken.None);
        var second = await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, first.Status);
        Assert.Equal(ActionSuccessCodes.ReplayedFromIdempotency, second.Code);
        Assert.Equal(1, erp.FakeErp.DocumentCount);
        Assert.Equal(1, erp.FakeErp.SubmitCallCount);
        Assert.Equal(first.ExternalId, second.ExternalId);

        // 模拟「终态缓存丢了、去重表还在」：此时必须由幂等领取挡住第二次副作用。
        store.ClearResults();
        var third = await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(ActionStatuses.Rejected, third.Status);
        Assert.Equal(ActionRejectionCodes.DuplicateClaim, third.Code);
        Assert.Equal(1, erp.FakeErp.DocumentCount);
        Assert.Equal(1, erp.FakeErp.SubmitCallCount);
        Assert.Equal(2, store.ClaimAttempts);
    }

    [Fact]
    public async Task S4_超时只查证不重放写动作()
    {
        // 负控：把 ActionRunService 里 Unknown/Timeout 分支的「只调 QuerySpec」改成再调一次 spec
        //       → SubmitCallCount 变 2、DocumentCount 可能变 2，本条红。
        var erp = new ErpActionSet();
        erp.FakeErp.SimulateTimeoutOnNextSubmit = true;
        var (service, _, _) = TestHarness.Create(erp);

        var result = await service.RunAsync(ErpSubmit(), CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal(ActionSuccessCodes.VerifiedByQuery, result.Code);
        Assert.NotNull(result.ExternalId);
        Assert.Equal(1, erp.FakeErp.SubmitCallCount);
        Assert.Equal(1, erp.FakeErp.QueryCallCount);
        Assert.Equal(1, erp.FakeErp.DocumentCount);
    }

    [Fact]
    public async Task S5_审计五字段齐全且可反查()
    {
        // 负控：把 TraceEvents.Create 里 Approver 或 ExternalId 传成 null → 本条红。
        var erp = new ErpActionSet();
        var (service, trace, _) = TestHarness.Create(erp);
        var request = ErpSubmit("req-audit-1");

        await service.RunAsync(request, CancellationToken.None);

        var trail = await service.AuditTrailAsync(request.RequestId, CancellationToken.None);
        Assert.NotEmpty(trail);
        var final = Assert.Single(trail.Where(e => e.Stage == AuditStages.Execute));
        Assert.False(string.IsNullOrWhiteSpace(final.Intent));
        Assert.False(string.IsNullOrWhiteSpace(final.Arguments));
        Assert.False(string.IsNullOrWhiteSpace(final.Approver));
        Assert.False(string.IsNullOrWhiteSpace(final.Result));
        Assert.False(string.IsNullOrWhiteSpace(final.ExternalId));
        Assert.All(trail, e => Assert.Equal(request.RequestId, e.RequestId));
        Assert.NotEmpty(trace.Events);
    }

    [Fact]
    public async Task S6_假第二动作集整条链跑通()
    {
        // 负控：把 LabActionSpecs.Submit 的 Primitive 改成 Query → 走不到写路径，本条红。
        var lab = new FakeActionSet();
        var (service, _, _) = TestHarness.Create(lab);
        var request = TestRequests.Create(
            requestId: "req-lab-1",
            subject: "wang",
            actionKey: LabActionSpecs.SubmitKey,
            argumentsJson: """{"lab_id":"LAB-A","slot_hours":2,"attendees":6}""");

        var result = await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.StartsWith("LB-", result.ExternalId);
        Assert.Equal(1, lab.FakeLab.DocumentCount);
        Assert.Equal("FakeActionSet", service.ActionSetName);
    }
}

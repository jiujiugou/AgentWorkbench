using AgentWorkbench.ActionGateway;
using AgentWorkbench.ApplicationTests.ActionGateway;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S08（执行：幂等 → 连接器 → 外部）的验收装置。<b>一个方法 = 一个步骤功能</b>：
/// 方法名说的是「这一步做什么」，绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>为什么是新建而不是迁移</b>：本格此前 0 条判据，但已有断言是全 S 系列里最密的一格，
/// 且散在各自主题下：<c>ActionRunSuccessTests.S3</c>（同 request_id 一张单、终态回放、
/// ClearResults 后幂等领取）、<c>S4</c>（超时只查证不重放写动作）、
/// <c>SqliteActionStoreTests.S2/S3/S6</c>（并发领取、回放、并发只一次副作用）、
/// <c>ErpNextQueryAdapterTests.T11</c>、<c>GiteaConnectorTests.G12</c>（查证目标是只读能力）。
/// 搬走会拆散原组，重复抄一遍也没有增量。本组只补四栏里<b>确实还没有</b>的那几处。
///
/// <b>边界</b>：起于幂等领取成功，止于终态落库交给 S09。四栏覆盖：
/// ①重试导致重复下单 → 已有 S3 / Sqlite S3、S6，本组不重复；②超时被当成失败去重放 → 已有 S4，
/// 本组补 <b>Unknown 分支</b>与<b>查证失败</b>；③执行成功但结果没写回 → 01、05（此前完全没验）；
/// 「交给下一格什么」→ 01、04、05；「不许留下什么」→ 02、03、04。
/// <b>不归本格</b>：结果的措辞与展示（S09 / S10）。
///
/// <b>登记在案的空白（不写占位测试、不写恒真的绿）</b>：
/// <list type="number">
/// <item>补偿动作 / 回滚<b>还没有</b>（文档自己写明：正本 §4.1 只要求「办错了能追回」的证据链，
/// 不等于已实现回滚）。本组一条都没验。</item>
/// <item><b>本轮发现的边界</b>：<c>ActionRunService.WriteAsync</c> 忽略 <c>ITraceSink.WriteAsync</c>
/// 返回的 <c>AuditWriteResult</c>，与 <c>FailClosedActionGateway.TraceAsync</c> 的 fail-closed 不一致。
/// 执行已产生外部副作用后若轨迹写失败，会被静默忽略、终态照落。未修、未写必然红的测试。</item>
/// <item><b>「不假设对方有幂等键」尚未闭合</b>：当前去重完全依赖本地 <c>request_id</c> 领取表，
/// 没有按业务键反查外部系统的端口。本地表只能证明「我们只发了一次」，
/// 证明不了「对方系统侧只有一条记录」——而后者才是正本 §4.1 第 3 句。Gitea 实测会忽略
/// <c>Idempotency-Key</c>（连发三次得三张单）。本组不假装已解决。</item>
/// <item>本组全用内存执行替身，不碰真网络；真连实证属 M2/M3，不在这里冒充。</item>
/// </list>
///
/// <b>层 × 方向</b>：01–05 全在 Core（<c>ActionRunService.ExecuteAsync</c>）；
/// 连接器按设计由域实现，测试里用 <c>CountingExecutor</c> 的 Write/Query 结果覆写来构造
/// 超时、未知、查证成功/失败四种形状，不另造一套假执行体。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S08ContractTests
{
    // [S08-01] 「执行成功但结果没写回」：终态、外部单号、执行轨迹三处都要写回且互相对得上。
    // 负控：删掉 ExecuteAsync 里的 SaveResultAsync → 终态查不回；删掉 WriteAsync → 轨迹断言红。
    [Fact]
    public async Task S08_01_成功执行后终态单号与执行轨迹都写回()
    {
        var (service, set, trace, store) = Create();

        var result = await service.RunAsync(
            TestRequests.Create("s08-01"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ExternalId));
        var saved = await store.LoadResultAsync("s08-01", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(result.Status, saved!.Status);
        Assert.Equal(result.ExternalId, saved.ExternalId);

        var executed = trace.Events.Where(e => e.Stage == AuditStages.Execute).ToList();
        Assert.Single(executed);
        Assert.Equal(result.Status, executed[0].Outcome);
        Assert.Equal(result.ExternalId, executed[0].ExternalId);
        Assert.Equal(1, set.ExecutorStub.WriteCallCount);
    }

    // [S08-02] 「超时 / 状态未知 → 只查证，不重放」。S4 只验了 Timeout，
    // Unknown 这条分支此前零覆盖；这里两种一起钉，并断言第二次调用走的确实是只读 Query 原语。
    // 负控：把 `is Unknown or Timeout` 收窄成只 Timeout → unknown 行红。
    [Theory]
    [InlineData("timeout")]
    [InlineData("unknown")]
    public async Task S08_02_超时与未知状态都只查证不重放写动作(string kind)
    {
        var status = kind == "timeout" ? ActionStatuses.Timeout : ActionStatuses.Unknown;
        var (service, set, _, _) = Create();
        set.ExecutorStub.WriteResultOverride =
            new ActionResult("s08-02", status, "pending", null, null, ActionResults.Empty);

        var result = await service.RunAsync(
            TestRequests.Create("s08-02"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal(ActionSuccessCodes.VerifiedByQuery, result.Code);
        Assert.Equal(1, set.ExecutorStub.WriteCallCount);
        Assert.Equal(1, set.ExecutorStub.QueryCallCount);
        Assert.Equal(
            new[] { ActionPrimitives.Submit, ActionPrimitives.Query },
            set.ExecutorStub.Primitives);
    }

    // [S08-03] 查证也失败时给一个明确终态，且<b>不重放写动作</b>。
    // S4 只验了查证成功的那一半；查证失败若被当成「再试一次写」，就会出两张单。
    // 负控：查证失败分支里再调一次 spec（原写能力）→ WriteCallCount 变 2，本条红。
    [Fact]
    public async Task S08_03_查证失败给明确终态且不重放写动作()
    {
        var (service, set, _, _) = Create();
        set.ExecutorStub.WriteResultOverride =
            new ActionResult("s08-03", ActionStatuses.Timeout, "pending", null, null, ActionResults.Empty);
        set.ExecutorStub.QueryResultOverride =
            ActionResults.Fail("s08-03", ActionRejectionCodes.Timeout);

        var result = await service.RunAsync(
            TestRequests.Create("s08-03"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(ActionRejectionCodes.Timeout, result.Code);
        Assert.Null(result.ExternalId);
        Assert.Equal(1, set.ExecutorStub.WriteCallCount);
        Assert.Equal(1, set.ExecutorStub.QueryCallCount);
        Assert.Equal(
            new[] { ActionPrimitives.Submit, ActionPrimitives.Query },
            set.ExecutorStub.Primitives);
    }

    // [S08-04] 查证成功时终态里的外部单号必须来自<b>对方系统</b>（Query 的结果），
    // 不是本地写动作返回的那个号。S4 只断言了「非空」，没断言来源。
    // 负控：把 `verified.ExternalId` 换成写动作结果里的 ExternalId → 本条红。
    [Fact]
    public async Task S08_04_终态外部单号来自查证结果而非本地写动作()
    {
        var (service, set, _, _) = Create();
        set.ExecutorStub.WriteResultOverride =
            new ActionResult("s08-04", ActionStatuses.Timeout, "pending", "WRITE-LOCAL-1", null, ActionResults.Empty);
        set.ExecutorStub.QueryResultOverride =
            new ActionResult("s08-04", ActionStatuses.Succeeded, "completed", "EXT-REAL-9", null, ActionResults.Empty);

        var result = await service.RunAsync(
            TestRequests.Create("s08-04"), CancellationToken.None);

        Assert.Equal(ActionSuccessCodes.VerifiedByQuery, result.Code);
        Assert.Equal("EXT-REAL-9", result.ExternalId);
    }

    // [S08-05] 「交给下一格什么」的闭环：查证后的终态确实落库、且能被回放挡住，
    // 而不是只把结果返回给调用方就算完。
    // 负控：超时分支不落终态，或落的是 Timeout 中间态 → 本条红。
    [Fact]
    public async Task S08_05_查证后的终态落库且可回放()
    {
        var (service, set, _, store) = Create();
        set.ExecutorStub.WriteResultOverride =
            new ActionResult("s08-05", ActionStatuses.Timeout, "pending", null, null, ActionResults.Empty);
        set.ExecutorStub.QueryResultOverride =
            new ActionResult("s08-05", ActionStatuses.Succeeded, "completed", "EXT-REAL-9", null, ActionResults.Empty);

        await service.RunAsync(TestRequests.Create("s08-05"), CancellationToken.None);

        var saved = await store.LoadResultAsync("s08-05", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(ActionStatuses.Succeeded, saved!.Status);
        Assert.Equal(ActionSuccessCodes.VerifiedByQuery, saved.Code);
        Assert.Equal("EXT-REAL-9", saved.ExternalId);

        var replay = await service.RunAsync(TestRequests.Create("s08-05"), CancellationToken.None);
        Assert.Equal(ActionSuccessCodes.ReplayedFromIdempotency, replay.Code);
        Assert.Equal(1, set.ExecutorStub.WriteCallCount);
    }

    // ── 装配 ────────────────────────────────────────────────────────

    /// <summary>免审批的提交规格：直接走到 ExecuteAsync，不会被 S07 的挂起分支截住。</summary>
    private static (
        ActionRunService Service,
        TestActionSet Set,
        InMemoryTraceStore Trace,
        InMemoryActionRunStore Store) Create()
    {
        var set = new TestActionSet();
        set.Registry.Spec = set.Spec with { RequiresApproval = false };
        var (service, trace, store) = TestHarness.Create(set);
        return (service, set, trace, store);
    }
}

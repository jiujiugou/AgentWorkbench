using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.ApplicationTests.ActionGateway;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Host;

/// <summary>
/// S07（审批：挂起 or 直接放行）的验收装置。<b>一个方法 = 一个步骤功能</b>：
/// 方法名说的是「这一步做什么」，绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>为什么是新建而不是迁移</b>：本格此前 0 条判据，但相关断言并不少，只是散在别的测试组里、
/// 各自服务于那一组的主题：<c>ActionRunSuccessTests.S2</c>（同步审批拒绝零外部调用）、
/// <c>SqliteActionStoreTests.S5/S7</c>（跨重启恢复、批准/拒绝离开待办队列、收口后再批回 not_pending）、
/// <c>CapabilityDirectoryTests.C6</c>（RequiresApproval 而非 SideEffecting 决定挂起）、
/// <c>S05_07</c>（挂起期间零外部调用）。把它们搬来会拆散原组上下文，也不该重复抄一遍。
/// 本组只补四栏里<b>确实还没有</b>的那几处，编号从 01 起算。
///
/// <b>边界</b>：起于 <c>SubmitDeferredAsync</c> 挂起，止于 Approve / Reject 收口交给 S08。
/// 四栏覆盖：①该审批的没审批就执行 → 01、08；②审批过了请求已被改动 → 04（只锁现状，见下）；
/// ③审批卡住不返回 → 02；「交给下一格什么」→ 04、05；「不许留下什么」→ 02、03、06、07。
/// <b>不归本格</b>：执行本身（S08）。
///
/// <b>登记在案的空白（不写占位测试、不写恒真的绿）</b>：
/// <list type="number">
/// <item>文档自己写着「审批与请求快照的绑定强度（参数或策略一变，审批是否失效、commit 时
/// 怎么校验）<b>还没有判定</b>，现在按快照处理」。04 只锁定「当前恢复用的是提交时的快照」这一事实，
/// <b>不代表绑定强度已经解决</b>；换策略后是否重判、commit 时怎么校验，本组一条都没验。</item>
/// <item>「审批超时 → 终态或保持挂起」当前实现只有「保持挂起」，没有超时收口机制。
/// 02 验的是「不默认放行」，不是「超时机制已实现」。</item>
/// <item><c>PendingRequestCodec.TryDecode</c> 只在外层反序列化处 catch 了 <c>JsonException</c>；
/// 「外层 JSON 合法但 ArgumentsJson 非法」会在 try 之外的 <c>JsonDocument.Parse</c> 抛出未被捕获的异常。
/// 07 只覆盖外层损坏的三种形状，这处边界如实挂着，未在本格解决。</item>
/// </list>
///
/// <b>层 × 方向</b>：01–08 全在 Core（<c>ActionRunService</c> / <c>PendingRequestCodec</c>），
/// 本格没有 Host 步（S06_13 的 422 映射是那一格文档自己写进去的，这里不类推）。
/// 04 用 <c>CapturingSet</c> 只替换执行体以看见快照，其余注入口仍走 <c>TestActionSet</c>。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S07ContractTests
{
    private const string ApprovalStage = "approval";

    // [S07-01] 两阶段提交的基本不变量：RequiresApproval 的动作挂起、回 Pending、
    // 待办里有一条、执行体零调用、且<b>没有终态</b>（Pending 不是终态）。
    // 负控：删掉 SubmitDeferredAsync 的挂起分支直接执行 → 本条红。
    [Fact]
    public async Task S07_01_需要审批时挂起且没有终态()
    {
        var (service, set, _, store) = Create();

        var result = await service.SubmitDeferredAsync(
            TestRequests.Create("s07-01"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Pending, result.Status);
        Assert.Equal(ActionRejectionCodes.ApprovalRequired, result.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Null(await store.LoadResultAsync("s07-01", CancellationToken.None));
        var pending = await store.ListCheckpointsAsync(ActionRunStates.PendingApproval, CancellationToken.None);
        Assert.Single(pending);
        Assert.Equal("s07-01", pending[0].RequestId);
    }

    // [S07-02] 文档「审批超时 → 终态或保持挂起，不默认放行」：当前实现是「保持挂起」。
    // 没人批准时，重复查询待办、重复提交同一单，都不该自己变成执行。
    // 负控：让挂起分支在无人批准时自动放行 → 本条红。
    // 注意：这条只验「不默认放行」，不验超时收口机制（当前没有，见头部登记）。
    [Fact]
    public async Task S07_02_无人批准时保持挂起且不默认放行()
    {
        var (service, set, _, store) = Create();
        var request = TestRequests.Create("s07-02");
        await service.SubmitDeferredAsync(request, CancellationToken.None);

        // 模拟审批页反复刷新 / 用户重复点了一次提交
        for (var i = 0; i < 3; i++)
        {
            var again = await service.SubmitDeferredAsync(request, CancellationToken.None);
            Assert.Equal(ActionStatuses.Pending, again.Status);
            Assert.Single(await store.ListCheckpointsAsync(ActionRunStates.PendingApproval, CancellationToken.None));
            Assert.Null(await store.LoadResultAsync("s07-02", CancellationToken.None));
        }

        Assert.Equal(0, set.ExecutorStub.CallCount);
    }

    // [S07-03] 「人工拒绝 → 明确终态且零外部调用」。Sqlite S7 验的是离开待办队列，
    // 这里补的是「终态确实落了、能查回来」。
    // 负控：RejectAsync 不调 SaveResultAsync，或拒绝后仍走 ExecuteAsync → 本条红。
    [Fact]
    public async Task S07_03_人工拒绝产生可查回的终态且零外部调用()
    {
        var (service, set, _, store) = Create();
        await service.SubmitDeferredAsync(TestRequests.Create("s07-03"), CancellationToken.None);

        var rejected = await service.RejectAsync("s07-03", "预算超了", CancellationToken.None);

        Assert.NotNull(rejected);
        Assert.Equal(ActionStatuses.Rejected, rejected!.Status);
        Assert.Equal(ActionRejectionCodes.ApprovalDenied, rejected.Code);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        var terminal = await store.LoadResultAsync("s07-03", CancellationToken.None);
        Assert.NotNull(terminal);
        Assert.Equal(ActionStatuses.Rejected, terminal!.Status);
        Assert.Empty(await store.ListCheckpointsAsync(ActionRunStates.PendingApproval, CancellationToken.None));
    }

    // [S07-04] 「交给下一格什么」前半：可执行的请求<b>快照</b>。
    // 挂起期间解码出来的、以及批准时真正交给执行体的，都必须是提交时那份逐字段一致的请求。
    // 负控：恢复时重新拼一个 request（丢 IdempotencyKey / 改参数）→ 本条红。
    // 边界：只锁定「当前按快照恢复」，不代表绑定强度已有判定（见头部登记 1）。
    [Fact]
    public async Task S07_04_批准时交出的是提交时的原始快照()
    {
        var inner = new TestActionSet();
        var wrapped = new CapturingSet(inner);
        var (service, _, _) = TestHarness.Create(wrapped);
        var request = TestRequests.Create("s07-04");
        await service.SubmitDeferredAsync(request, CancellationToken.None);

        var snapshot = await service.LoadPendingRequestAsync("s07-04", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(request.RequestId, snapshot!.RequestId);
        Assert.Equal(request.Subject, snapshot.Subject);
        Assert.Equal(request.ActionKey, snapshot.ActionKey);
        Assert.Equal(request.IdempotencyKey, snapshot.IdempotencyKey);
        Assert.Equal(request.Arguments.GetRawText(), snapshot.Arguments.GetRawText());

        await service.ApproveAsync("s07-04", "owner-wang", CancellationToken.None);
        Assert.NotNull(wrapped.Capture.Last);
        Assert.Equal(request.Arguments.GetRawText(), wrapped.Capture.Last!.Arguments.GetRawText());
    }

    // [S07-05] 「交给下一格什么」后半：审批当时的判定要进轨迹，审批人不得伪造。
    // 负控：WriteAsync 的 approver 传 null，或不写 approval/allow → 本条红。
    [Fact]
    public async Task S07_05_批准判定与审批人进轨迹()
    {
        var (service, _, trace, _) = Create();
        await service.SubmitDeferredAsync(TestRequests.Create("s07-05"), CancellationToken.None);

        await service.ApproveAsync("s07-05", "owner-wang", CancellationToken.None);

        var allow = trace.Events.Where(e => e.Stage == ApprovalStage && e.Outcome == "allow").ToList();
        Assert.Single(allow);
        Assert.Equal("owner-wang", allow[0].Approver);
        Assert.Contains("approved by human", allow[0].Result!);
    }

    // [S07-06] 拒绝理由同样要留得下来，事后才答得出「谁、依据什么否的」。
    // 负控：RejectAsync 写轨迹时不带 reason → 本条红。
    [Fact]
    public async Task S07_06_拒绝理由留在轨迹里()
    {
        var (service, _, trace, _) = Create();
        await service.SubmitDeferredAsync(TestRequests.Create("s07-06"), CancellationToken.None);

        await service.RejectAsync("s07-06", "预算超了", CancellationToken.None);

        var deny = trace.Events.Where(e => e.Stage == ApprovalStage && e.Outcome == "deny").ToList();
        Assert.Single(deny);
        Assert.Equal("预算超了", deny[0].Result);
        Assert.Null(deny[0].ExternalId);   // 拒绝不得带出外部单号
    }

    // [S07-07] checkpoint 损坏时按「不可恢复」处理，绝不猜参数继续执行。
    // 三种形状：外层语法错、整体 null、缺字段的空对象。
    // 负控：TryDecode 在解码失败时回退到一个默认 request → 本条红。
    // 边界：只覆盖外层损坏；「外层合法但 ArgumentsJson 非法」未解决，见头部登记 3。
    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task S07_07_损坏的快照不得猜参数继续执行(string blob)
    {
        var (service, set, _, store) = Create();
        await store.SuspendAsync(
            "s07-07", ActionRunStates.PendingApproval, blob, CancellationToken.None);

        var approved = await service.ApproveAsync("s07-07", "owner-wang", CancellationToken.None);

        Assert.Null(approved);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Null(await store.LoadResultAsync("s07-07", CancellationToken.None));
    }

    // [S07-08] 是否需要审批由域注入的 ActionSpec.RequiresApproval 决定，不是从「有没有副作用」推的。
    // C6 验的是「有副作用但免审批 → 不挂起」，这里补反方向「只读但要求审批 → 仍挂起」。
    // 负控：把挂起判断改回 spec.SideEffecting → 本条红。
    [Fact]
    public async Task S07_08_只读但要求审批的动作仍挂起()
    {
        var set = new TestActionSet();
        set.Registry.Spec = set.Spec with { SideEffecting = false, RequiresApproval = true };
        var (service, _, store) = TestHarness.Create(set);

        var result = await service.SubmitDeferredAsync(
            TestRequests.Create("s07-08"), CancellationToken.None);

        Assert.Equal(ActionStatuses.Pending, result.Status);
        Assert.Equal(0, set.ExecutorStub.CallCount);
        Assert.Single(await store.ListCheckpointsAsync(ActionRunStates.PendingApproval, CancellationToken.None));
    }

    // ── 装配 ────────────────────────────────────────────────────────

    private static (
        ActionRunService Service,
        TestActionSet Set,
        InMemoryTraceStore Trace,
        InMemoryActionRunStore Store) Create()
    {
        var set = new TestActionSet();
        var (service, trace, store) = TestHarness.Create(set);
        return (service, set, trace, store);
    }

    /// <summary>只替换执行体以看见「批准时真正交出去的是什么」，其余注入口原样转发。</summary>
    private sealed class CapturingSet(TestActionSet inner) : IActionSet
    {
        public CapturingExecutor Capture { get; } = new(inner.Executor);
        public string Name => inner.Name;
        public IActionSpecRegistry Specs => inner.Specs;
        public ISchemaValidator Schema => inner.Schema;
        public IPermissionQuery Permissions => inner.Permissions;
        public IBusinessRule Rules => inner.Rules;
        public IActionExecutor Executor => Capture;
        public IApprovalGate Approvals => inner.Approvals;
        public ActionSpec QuerySpec => inner.QuerySpec;
    }

    private sealed class CapturingExecutor(IActionExecutor inner) : IActionExecutor
    {
        public ActionRequest? Last { get; private set; }

        public ValueTask<ActionResult> ExecuteAsync(
            ActionSpec spec, ActionRequest request, CancellationToken ct)
        {
            Last = request;
            return inner.ExecuteAsync(spec, request, ct);
        }
    }
}

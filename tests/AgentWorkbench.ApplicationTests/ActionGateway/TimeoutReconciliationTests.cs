namespace AgentWorkbench.ApplicationTests.ActionGateway;

using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Mcp;
using Xunit;

/// <summary>
/// 第 4 句端到端：写动作遇到传输故障 → 只查证、不重放 → 终态 verified_by_query。
/// 这里特意不用 FakeErp 的 SimulateTimeout（那是另一条路径），而是真的让 McpActionExecutor
/// 收到 Unavailable → Unknown，证明真连链路上这条分支确实可达。
/// 负控：把 McpActionExecutor 的 Unavailable→Unknown 去掉，本条红（查证分支不触发）。
/// </summary>
public sealed class TimeoutReconciliationTests
{
    [Fact]
    public async Task 传输故障后只查证不重放写动作()
    {
        var writes = new CountingInvoker(
            ErpMcpBindings.DefaultCancelTool,
            ToolInvocationResult.Failure(ToolInvocationCodes.Unavailable, "Cannot reach Frappe"));
        var reads = new CountingInvoker(
            ErpMcpBindings.DefaultQueryTool,
            ToolInvocationResult.Success("""{"data":{"name":"PUR-ORD-2026-00042","docstatus":2}}"""));

        var set = new ReconcileActionSet(writes, reads);
        var (service, _, _) = TestHarness.Create(set);
        var request = new ActionRequest(
            "req-reconcile", "wang", ErpActionSpecs.CancelKey,
            JsonDocument.Parse("""{"document_id":"PUR-ORD-2026-00042"}""").RootElement.Clone(),
            "idem-reconcile");

        var result = await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, result.Status);
        Assert.Equal(ActionSuccessCodes.VerifiedByQuery, result.Code);
        Assert.Equal("PUR-ORD-2026-00042", result.ExternalId);
        Assert.Equal(1, writes.CallCount);  // 写动作只发生一次，绝不重放
        Assert.Equal(1, reads.CallCount);   // 查证只读发生一次
    }

    [Fact]
    public async Task 对方明确拒绝不触发查证()
    {
        // 负控点：若把 Unavailable→Unknown 放宽成所有失败都 Unknown，本条红（reads 会被调 1 次）。
        var writes = new CountingInvoker(
            ErpMcpBindings.DefaultCancelTool,
            ToolInvocationResult.Failure(ToolInvocationCodes.Failed, "417 MandatoryError"));
        var reads = new CountingInvoker(
            ErpMcpBindings.DefaultQueryTool,
            ToolInvocationResult.Success("""{"data":{"name":"x"}}"""));

        var set = new ReconcileActionSet(writes, reads);
        var (service, _, _) = TestHarness.Create(set);
        var request = new ActionRequest(
            "req-reject", "wang", ErpActionSpecs.CancelKey,
            JsonDocument.Parse("""{"document_id":"PUR-ORD-2026-00042"}""").RootElement.Clone(),
            "idem-reject");

        var result = await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(ActionStatuses.Failed, result.Status);
        Assert.Equal(1, writes.CallCount);
        Assert.Equal(0, reads.CallCount);  // 明确拒绝不需要查证
    }

    private sealed class ReconcileActionSet : IActionSet
    {
        public ReconcileActionSet(IToolInvoker writes, IToolInvoker reads)
        {
            Executor = new DispatchingExecutor(writes, reads);
        }

        public string Name => "Reconcile";
        public IActionSpecRegistry Specs => new ErpActionSpecRegistry();
        public ISchemaValidator Schema => new ErpSchemaValidator();
        public IPermissionQuery Permissions => new ErpPermissionPdp();
        public IBusinessRule Rules => new ErpBusinessRule();
        public IActionExecutor Executor { get; }
        public IApprovalGate Approvals => new ErpApprovalGate();
        public ActionSpec QuerySpec => ErpActionSpecs.Query;

        /// <summary>同一个执行体按 spec 分派：写走 writes、查证走 reads，和真连时一个 executor 处理两者一致。</summary>
        private sealed class DispatchingExecutor : IActionExecutor
        {
            private readonly McpActionExecutor _write;
            private readonly McpActionExecutor _read;

            public DispatchingExecutor(IToolInvoker writes, IToolInvoker reads)
            {
                _write = new McpActionExecutor(writes, [ErpMcpBindings.Cancel()]);
                _read = new McpActionExecutor(reads, [ErpMcpBindings.Query()]);
            }

            public ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
                => spec.ActionKey == ErpActionSpecs.QueryKey
                    ? _read.ExecuteAsync(spec, request, ct)
                    : _write.ExecuteAsync(spec, request, ct);
        }
    }

    private sealed class CountingInvoker : IToolInvoker
    {
        private readonly string _toolName;
        private readonly ToolInvocationResult _reply;

        public CountingInvoker(string toolName, ToolInvocationResult reply)
        {
            _toolName = toolName;
            _reply = reply;
        }

        public int CallCount { get; private set; }

        public bool Has(string toolName) => toolName == _toolName;

        public ValueTask<ToolInvocationResult> InvokeAsync(
            string toolName, JsonElement arguments, CancellationToken ct)
        {
            CallCount++;
            return ValueTask.FromResult(_reply);
        }
    }
}

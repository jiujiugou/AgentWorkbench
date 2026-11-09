using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Ports;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// M1 验收装置：可控的四道校验 stub 与计数执行体。
/// 关键：CountingExecutor 记录真实调用次数，「四道不过时 executor 零调用」是可被证伪的行为断言，
/// 不是「结构上没有引用」造成的恒真。
/// </summary>
public sealed class StubSchemaValidator : ISchemaValidator
{
    public bool Valid { get; set; } = true;
    public int CallCount { get; private set; }

    public ValueTask<SchemaDecision> ValidateAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        CallCount++;
        return ValueTask.FromResult(new SchemaDecision(Valid, Valid ? "stub allow" : "stub deny"));
    }
}

public sealed class StubPermissionQuery : IPermissionQuery
{
    public bool Allow { get; set; } = true;
    public int CallCount { get; private set; }

    /// <summary>最近一次收到的权限查询，用于核对「谁在申请」不是模型填的。</summary>
    public PermissionQuery? LastQuery { get; private set; }

    public ValueTask<PermissionDecision> QueryAsync(PermissionQuery query, CancellationToken ct)
    {
        CallCount++;
        LastQuery = query;
        return ValueTask.FromResult(new PermissionDecision(Allow, Allow ? "stub allow" : "stub deny"));
    }
}

public sealed class StubBusinessRule : IBusinessRule
{
    public bool Allow { get; set; } = true;
    public int CallCount { get; private set; }

    public ValueTask<RuleDecision> EvaluateAsync(ActionRequest request, CancellationToken ct)
    {
        CallCount++;
        return ValueTask.FromResult(new RuleDecision(Allow, Allow ? "stub allow" : "stub deny", null));
    }
}

public sealed class StubApprovalGate : IApprovalGate
{
    public bool Approved { get; set; } = true;
    public int CallCount { get; private set; }

    public ValueTask<ApprovalDecision> RequestAsync(ActionRequest request, CancellationToken ct)
    {
        CallCount++;
        return ValueTask.FromResult(new ApprovalDecision(
            Approved,
            Approved ? "审批人·测试" : null,
            Approved ? "stub approved" : "stub denied"));
    }
}

public sealed class StubSpecRegistry : IActionSpecRegistry
{
    public ActionSpec? Spec { get; set; }

    /// <summary>
    /// 默认 false（不按名匹配，保持既有测试行为）。设为 true 才按 ActionKey 严格查找，
    /// 用于验证「清单外的能力名会被拒绝」——否则 stub 恒返回同一个 spec，那条断言是假绿。
    /// </summary>
    public bool MatchKey { get; set; }

    public ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct)
        => ValueTask.FromResult(
            MatchKey && !string.Equals(Spec?.ActionKey, actionKey, StringComparison.Ordinal) ? null : Spec);

    public ValueTask<IReadOnlyList<ActionSpec>> ListAsync(CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<ActionSpec>>(Spec is null ? [] : [Spec]);
}

public sealed class CountingExecutor : IActionExecutor
{
    public int CallCount { get; private set; }
    public int WriteCallCount { get; private set; }
    public int QueryCallCount { get; private set; }
    public List<string> Primitives { get; } = [];
    public ActionResult? WriteResultOverride { get; set; }
    public ActionResult? QueryResultOverride { get; set; }

    private static ActionResult Success(string requestId)
        => new(requestId, ActionStatuses.Succeeded, "completed", "EXT-0001", null,
            JsonSerializer.SerializeToElement(new { ok = true }));

    public ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        CallCount++;
        Primitives.Add(spec.Primitive);
        if (spec.Primitive == ActionPrimitives.Query)
        {
            QueryCallCount++;
            return ValueTask.FromResult(QueryResultOverride ?? Success(request.RequestId));
        }
        WriteCallCount++;
        return ValueTask.FromResult(WriteResultOverride ?? Success(request.RequestId));
    }
}

public sealed class TestActionSet : IActionSet
{
    public TestActionSet()
    {
        Registry.Spec = Spec;
    }

    public StubSpecRegistry Registry { get; } = new();
    public StubSchemaValidator Validator { get; } = new();
    public StubPermissionQuery Pdp { get; } = new();
    public StubBusinessRule Rule { get; } = new();
    public StubApprovalGate Approval { get; } = new();
    public CountingExecutor ExecutorStub { get; } = new();

    public ActionSpec Spec { get; } = new("test.submit", "test.schema", true, ActionPrimitives.Submit)
    {
        DisplayName = "测试提交动作",
        Description = "测试用动作规格，仅用于验证网关行为，不代表任何业务能力。",
        RiskLevel = RiskLevels.High,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = "{}",
    };

    public ActionSpec QuerySpec { get; } = new("test.query", "test.schema", false, ActionPrimitives.Query)
    {
        DisplayName = "测试查询动作",
        Description = "测试用只读规格，超时查证时作为 Query 原语的目标。",
        RiskLevel = RiskLevels.Low,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = "{}",
    };

    public string Name => "TestActionSet";
    public IActionSpecRegistry Specs => Registry;
    public ISchemaValidator Schema => Validator;
    public IPermissionQuery Permissions => Pdp;
    public IBusinessRule Rules => Rule;
    public IActionExecutor Executor => ExecutorStub;
    public IApprovalGate Approvals => Approval;
}

public static class TestRequests
{
    public static ActionRequest Create(
        string requestId = "req-1",
        string subject = "wang",
        string actionKey = "test.submit",
        string argumentsJson = """{"document_id":"PO-2026-1108","amount":48000}""")
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        return new ActionRequest(requestId, subject, actionKey, doc.RootElement.Clone(), requestId);
    }
}

public static class TestHarness
{
    public static (ActionRunService Service, InMemoryTraceStore Trace, InMemoryActionRunStore Store) Create(IActionSet set)
    {
        var trace = new InMemoryTraceStore();
        var store = new InMemoryActionRunStore();
        return (new ActionRunService(set, trace, store, trace), trace, store);
    }
}

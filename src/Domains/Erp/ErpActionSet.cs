namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// 组合根只注入这一个对象即可启用第一动作域（M1 设计 §5.1 / §7）。
/// 换域时替换成另一套 IActionSet 实现，src/Core 一行不改（AC-01）。
/// </summary>
public sealed class ErpActionSet : IActionSet
{
    private readonly IActionExecutor _executor;

    /// <summary>
    /// 无参构造：执行体指向内存假件。仅用于本地演练与判据测试，
    /// 生产/真连路径必须由组合根传入真执行体（见 <see cref="ErpNext.ErpNextQueryAdapter"/>）。
    /// </summary>
    public ErpActionSet() : this(null)
    {
    }

    /// <summary>
    /// 传入真执行体即「接真 ERP」；<c>null</c> 退回内存假件。
    /// 假件始终构造出来，是因为判据测试要读它的调用计数（<c>FakeErp.SubmitCallCount</c>），
    /// 而真连时它必须**不被当作执行体使用** —— 这一点由 <see cref="Executor"/> 的唯一取值点保证。
    /// </summary>
    public ErpActionSet(IActionExecutor? executor)
    {
        // 假件始终构造：判据测试要读它的调用计数，真连时靠 Executor 不指向它来保证不被使用。
        FakeErp = new InMemoryFakeErp();
        _executor = executor ?? FakeErp;

        ApprovalGate = new ErpApprovalGate();
        Pdp = new ErpPermissionPdp();
        Rule = new ErpBusinessRule();
        SchemaValidator = new ErpSchemaValidator();
        Registry = new ErpActionSpecRegistry();
    }

    public InMemoryFakeErp FakeErp { get; }
    public ErpApprovalGate ApprovalGate { get; }
    public ErpPermissionPdp Pdp { get; }
    public ErpBusinessRule Rule { get; }
    public ErpSchemaValidator SchemaValidator { get; }
    public ErpActionSpecRegistry Registry { get; }

    public string Name => "Erp";
    public IActionSpecRegistry Specs => Registry;
    public ISchemaValidator Schema => SchemaValidator;
    public IPermissionQuery Permissions => Pdp;
    public IBusinessRule Rules => Rule;
    public IActionExecutor Executor => _executor;
    public IApprovalGate Approvals => ApprovalGate;
    public ActionSpec QuerySpec => ErpActionSpecs.Query;
}

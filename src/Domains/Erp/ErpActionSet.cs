namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// 组合根只注入这一个对象即可启用第一动作域（M1 设计 §5.1 / §7）。
/// 换域时替换成另一套 IActionSet 实现，src/Core 一行不改（AC-01）。
/// </summary>
public sealed class ErpActionSet : IActionSet
{
    public ErpActionSet()
    {
        FakeErp = new InMemoryFakeErp();
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
    public IActionExecutor Executor => FakeErp;
    public IApprovalGate Approvals => ApprovalGate;
    public ActionSpec QuerySpec => ErpActionSpecs.Query;
}

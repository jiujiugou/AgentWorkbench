namespace AgentWorkbench.LabBooking;

using AgentWorkbench.Ports;

/// <summary>
/// 组合根只替换这一个对象即可启用第二动作域（M1 设计 §5.1 / §5.3 / §7）。
/// 换域时把组合根从第一动作域换成 FakeActionSet，src/Core 一行不改（AC-01）。
/// </summary>
public sealed class FakeActionSet : IActionSet
{
    public FakeActionSet()
    {
        FakeLab = new InMemoryFakeLab();
        ApprovalGate = new LabApprovalGate();
        Pdp = new LabPermissionPdp();
        Rule = new LabBusinessRule();
        SchemaValidator = new LabSchemaValidator();
        Registry = new LabActionSpecRegistry();
    }

    public InMemoryFakeLab FakeLab { get; }
    public LabApprovalGate ApprovalGate { get; }
    public LabPermissionPdp Pdp { get; }
    public LabBusinessRule Rule { get; }
    public LabSchemaValidator SchemaValidator { get; }
    public LabActionSpecRegistry Registry { get; }

    public string Name => "FakeActionSet";
    public IActionSpecRegistry Specs => Registry;
    public ISchemaValidator Schema => SchemaValidator;
    public IPermissionQuery Permissions => Pdp;
    public IBusinessRule Rules => Rule;
    public IActionExecutor Executor => FakeLab;
    public IApprovalGate Approvals => ApprovalGate;
    public ActionSpec QuerySpec => LabActionSpecs.Query;
}

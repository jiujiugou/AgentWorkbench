namespace AgentWorkbench.Gitea;

/// <summary>
/// 组合根只注入这一个对象即可启用 Gitea 动作域（AC-01 的第三个真实载体）。
/// 无参构造故意是「未配置」状态：任何动作都会 fail-closed 且零 HTTP，
/// 生产必须由组合根显式传入 GiteaOptions。
/// </summary>
public sealed class GiteaActionSet : IActionSet
{
    public GiteaActionSet() : this(GiteaOptions.Unconfigured)
    {
    }

    public GiteaActionSet(GiteaOptions options, IGiteaTransport? transport = null)
    {
        Options = options;
        Adapter = new GiteaAdapter(options, transport);
        ApprovalGate = new GiteaApprovalGate();
        Pdp = new GiteaPermissionPdp();
        Rule = new GiteaBusinessRule();
        SchemaValidator = new GiteaSchemaValidator();
        Registry = new GiteaActionSpecRegistry();
    }

    public GiteaOptions Options { get; }
    public GiteaAdapter Adapter { get; }
    public GiteaApprovalGate ApprovalGate { get; }
    public GiteaPermissionPdp Pdp { get; }
    public GiteaBusinessRule Rule { get; }
    public GiteaSchemaValidator SchemaValidator { get; }
    public GiteaActionSpecRegistry Registry { get; }

    public string Name => "Gitea";
    public IActionSpecRegistry Specs => Registry;
    public ISchemaValidator Schema => SchemaValidator;
    public IPermissionQuery Permissions => Pdp;
    public IBusinessRule Rules => Rule;
    public IActionExecutor Executor => Adapter;
    public IApprovalGate Approvals => ApprovalGate;

    /// <summary>超时只查证，不重放写动作。</summary>
    public ActionSpec QuerySpec => GiteaActionSpecs.QuerySpec;
}

using Microsoft.Extensions.Options;

namespace AgentWorkbench.Safety;

/// <summary>工具审批级别：某个可见的工具，执行前要不要先问人。</summary>
public enum ToolApprovalMode
{
    /// <summary>直接执行，不打扰用户。</summary>
    Auto = 0,

    /// <summary>执行前挂起，等人工确认；无应答 / 超时一律按拒绝处理。</summary>
    Ask = 1,

    /// <summary>直接拒绝，工具绝不被执行。</summary>
    Deny = 2
}

/// <summary>
/// 工具审批策略：决定某个工具名的审批级别。
/// </summary>
/// <remarks>
/// 与 <see cref="IToolPolicy"/>（能力白名单）<b>并列且正交，绝不合并</b>：
/// 「能不能用」与「要不要先问人」是两个独立维度 —— 一个工具可以「可见也允许，但每次都要确认」，
/// 也可以「可见也允许，且不问」。揉进同一个接口，这两种状态就再也表达不出来了
/// （这正是把 Codex 的沙箱轴与审批轴拆开的意义）。
/// </remarks>
public interface IToolApprovalPolicy
{
    /// <summary>某个工具名对应的审批级别。</summary>
    ToolApprovalMode Decision(string toolName);
}

/// <inheritdoc />
/// <remarks>
/// 与 <see cref="ToolPolicy"/> 同构：持 <see cref="IOptionsMonitor{T}"/> 而非快照，
/// 审批级别改 appsettings.json（<c>Harness:Approval</c>）即热重载、下个 run 生效，零停服。
/// </remarks>
public sealed class ToolApprovalPolicy : IToolApprovalPolicy
{
    private readonly IOptionsMonitor<ToolApprovalOptions> _options;

    public ToolApprovalPolicy(IOptionsMonitor<ToolApprovalOptions> options) => _options = options;

    public ToolApprovalMode Decision(string toolName)
    {
        var options = _options.CurrentValue;
        return options.Overrides.TryGetValue(toolName, out var mode) ? mode : options.Default;
    }
}

/// <summary>
/// 全部 <see cref="ToolApprovalMode.Auto"/> 的审批策略：
/// 把「这一处不做审批」写成<b>明确的取值</b>，而不是让调用方传 <c>null</c> 表达沉默的默认。
/// </summary>
/// <remarks>
/// 生产装配由 DI 注入 <see cref="ToolApprovalPolicy"/>；本类型供测试与
/// 「本场景不关心审批」的组合点使用，避免 <c>null</c> 在安全语义上产生歧义。
/// </remarks>
public sealed class AutoToolApprovalPolicy : IToolApprovalPolicy
{
    /// <summary>无状态单例。</summary>
    public static AutoToolApprovalPolicy Instance { get; } = new();

    private AutoToolApprovalPolicy()
    {
    }

    public ToolApprovalMode Decision(string toolName) => ToolApprovalMode.Auto;
}

/// <summary>工具审批配置（配置节 <c>Harness:Approval</c>）。</summary>
/// <remarks>
/// 默认 <see cref="ToolApprovalMode.Auto"/>：<b>不显式提升就永不打扰用户</b>。
/// 全默认 Ask 会让第一次使用就被确认框淹没；逐个工具显式提升，改配置即生效（无需停服）。
/// </remarks>
public sealed class ToolApprovalOptions
{
    public const string SectionName = "Harness:Approval";

    /// <summary>未在 <see cref="Overrides"/> 中列出的工具的默认审批级别。</summary>
    public ToolApprovalMode Default { get; set; } = ToolApprovalMode.Auto;

    /// <summary>按工具名的级别覆盖（工具名 → 级别）。</summary>
    public Dictionary<string, ToolApprovalMode> Overrides { get; set; } = new(StringComparer.Ordinal);
}

using AgentWorkbench.ToolExecution;
using Xunit;

namespace AgentWorkbench.UnitTests.Tools;

// 审批轴的策略层：只回答「某个工具该 Auto / Ask / Deny」，不碰任何执行细节。
// 与能力白名单（ToolPolicy）并列且正交——本文件与 ToolCatalogTests 没有任何共用状态，
// 正是「两维不许粘回一起」的可执行证据。
public sealed class ToolApprovalPolicyTests
{
    private static ToolApprovalPolicy Build(ToolApprovalOptions options)
        => new(new MutableOptionsMonitor<ToolApprovalOptions>(options));

    [Fact]
    public void Decision_DefaultsToAuto_AndOverridesByName()
    {
        var policy = Build(new ToolApprovalOptions
        {
            Default = ToolApprovalMode.Auto,
            Overrides =
            {
                ["shell"] = ToolApprovalMode.Deny,
                ["search_knowledge"] = ToolApprovalMode.Ask
            }
        });

        // 默认不打扰：没显式提升的工具一律 Auto —— 否则第一次使用就被确认框淹没。
        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("get_current_time"));
        Assert.Equal(ToolApprovalMode.Deny, policy.Decision("shell"));
        Assert.Equal(ToolApprovalMode.Ask, policy.Decision("search_knowledge"));
    }

    /// <summary>
    /// 审批级别住进 appsettings.json（<c>Harness:Approval</c>）后，改配置下个 run 生效、无需停服。
    /// 负控意义：若本策略退回持 <c>IOptions</c>（静态快照），第二组断言必须失败。
    /// </summary>
    [Fact]
    public void Decision_ReflectsConfigChangeWithoutRestart()
    {
        var monitor = new MutableOptionsMonitor<ToolApprovalOptions>(
            new ToolApprovalOptions { Default = ToolApprovalMode.Auto });
        var policy = new ToolApprovalPolicy(monitor);

        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("shell"));

        // 模拟热重载：运维把 shell 提升为 Deny、默认整体提升为 Ask
        monitor.Update(new ToolApprovalOptions
        {
            Default = ToolApprovalMode.Ask,
            Overrides = { ["shell"] = ToolApprovalMode.Deny }
        });

        Assert.Equal(ToolApprovalMode.Deny, policy.Decision("shell"));   // 覆盖优先
        Assert.Equal(ToolApprovalMode.Ask, policy.Decision("calculator")); // 新默认生效
    }
}

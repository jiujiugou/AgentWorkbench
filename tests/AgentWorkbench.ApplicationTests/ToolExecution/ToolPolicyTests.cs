using AgentWorkbench.Runtime.Tools;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

/// <summary>
/// 白名单匹配语义：精确名 + 「尾部 *」前缀通配。
/// </summary>
/// <remarks>
/// 加通配是为了 MCP：外部 server 的工具名不由我们控制，逐个列名会在 server 升级加工具时
/// 静默失效（表现为「新工具调不到」，没有任何报错）。但通配只允许出现在末尾——
/// 一旦允许中间通配，读配置的人就无法回答「到底放行了哪些工具」，那个白名单就没法审了。
/// </remarks>
public class ToolPolicyTests
{
    private static ToolPolicy Create(params string[] whitelist)
    {
        var options = new ToolPolicyOptions { Whitelist = [.. whitelist] };
        var monitor = new Mock<IOptionsMonitor<ToolPolicyOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        return new ToolPolicy(monitor.Object);
    }

    // [F2.1] 精确名只放行它自己。
    // 负控：把精确比较改成 StartsWith，S1 立刻红（"calc" 会放行 "calculator"）。
    [Fact]
    public void S1_精确名只放行同名工具()
    {
        var policy = Create("calculator", "search_knowledge");

        Assert.True(policy.IsAllowed("calculator"));
        Assert.True(policy.IsAllowed("search_knowledge"));
        Assert.False(policy.IsAllowed("calc"));
        Assert.False(policy.IsAllowed("calculator_plus"));
        Assert.False(policy.IsAllowed("propose_action"));
    }

    // [F2.1] 尾部 * 按前缀放行，且只管自己那个前缀。
    // 负控：把 rule[^1] == '*' 的分支删掉，S2 全红——通配退化成精确匹配，一个 MCP 工具都放不进来。
    [Fact]
    public void S2_尾部通配只放行该前缀下的工具()
    {
        var policy = Create("mcp__erpnext__*");

        Assert.True(policy.IsAllowed("mcp__erpnext__get_document"));
        Assert.True(policy.IsAllowed("mcp__erpnext__list_documents"));
        Assert.True(policy.IsAllowed("mcp__erpnext__"));

        // 前缀必须连分隔符一起匹配：别的 server 同名工具不能蹭进来。
        Assert.False(policy.IsAllowed("mcp__gitea__get_document"));
        Assert.False(policy.IsAllowed("mcp_erpnext__get_document"));
        Assert.False(policy.IsAllowed("get_document"));
    }

    // [F2.1] 单星号 = 放行全部（显式配置的结果，不是默认值）。
    // 这条刻意写成断言：如果哪天有人把「空白名单」误当成「全放行」，S3 会红。
    [Fact]
    public void S3_单星号放行全部但空白名单不放行任何工具()
    {
        Assert.True(Create("*").IsAllowed("whatever"));
        Assert.True(Create("*").IsAllowed("mcp__erpnext__delete_document"));

        var empty = Create();
        Assert.False(empty.IsAllowed("calculator"));
        Assert.False(empty.IsAllowed(""));
    }

    // [F2.1] 空名与空白规则都不参与匹配。
    // 负控：去掉 IsNullOrWhiteSpace(rule) 的跳过，" " 这条规则会变成前缀空串 → 放行一切。
    [Fact]
    public void S4_空白规则与空工具名都不放行()
    {
        var policy = Create(" ", "", "calculator");

        Assert.False(policy.IsAllowed(""));
        Assert.False(policy.IsAllowed("anything"));
        Assert.True(policy.IsAllowed("calculator"));
    }
}

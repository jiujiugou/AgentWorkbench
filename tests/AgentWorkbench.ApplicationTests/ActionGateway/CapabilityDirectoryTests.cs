using System.Text.Json;
using System.Text.RegularExpressions;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.ApplicationTests.Runtime;
using AgentWorkbench.Erp;
using AgentWorkbench.Gitea;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// 方案第 3 步「业务能力目录」的验收。
/// 每条断言都指向「目录能不能被列举 / 够不够格暴露给模型 / 会不会把业务词带进底座」。
/// 负控路径见 docs/设计/M2-真实ERPNext只读接入设计.md §12 与本轮负控自证报告。
/// </summary>
public sealed class CapabilityDirectoryTests
{
    // C1：目录必须能列出全部能力。只有 FindAsync 而列不出全部，第 4 步（暴露给 Agent）永远做不了。
    [Fact]
    public async Task C1_三个动作域的能力都能被列举且能按名取回()
    {
        IActionSpecRegistry[] registries =
        [
            new ErpActionSpecRegistry(),
            new LabActionSpecRegistry(),
            new GiteaActionSpecRegistry(),
        ];

        foreach (var registry in registries)
        {
            var all = await registry.ListAsync(CancellationToken.None);
            Assert.Equal(4, all.Count);
            foreach (var spec in all)
                Assert.Same(spec, await registry.FindAsync(spec.ActionKey, CancellationToken.None));
        }
    }

    // C2：不能出现「有 ActionKey 但没描述 / 没参数 Schema」的半成品能力。
    // 负控：删任一 spec 的 Description 或把 InputSchemaJson 写成非 JSON → 本条红。
    [Fact]
    public async Task C2_每个能力都有描述风险版本与合法参数Schema()
    {
        IActionSpecRegistry[] registries =
        [
            new ErpActionSpecRegistry(),
            new LabActionSpecRegistry(),
            new GiteaActionSpecRegistry(),
        ];

        foreach (var registry in registries)
        {
            foreach (var spec in await registry.ListAsync(CancellationToken.None))
            {
                Assert.False(string.IsNullOrWhiteSpace(spec.DisplayName), spec.ActionKey);
                Assert.False(string.IsNullOrWhiteSpace(spec.Description), spec.ActionKey);
                Assert.True(spec.Description.Length >= 20, $"{spec.ActionKey} 的 Description 不足以让模型判断用途");
                Assert.Contains(spec.RiskLevel, RiskLevelValues.All);
                Assert.False(string.IsNullOrWhiteSpace(spec.Version), spec.ActionKey);

                using var schema = JsonDocument.Parse(spec.InputSchemaJson);
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
                Assert.True(schema.RootElement.TryGetProperty("type", out _), spec.ActionKey);
            }
        }
    }

    // C3：RiskLevel 只用于展示，不参与决策。
    // 同为 medium，一个要审批一个不要 —— 说明审批不是从 risk 推出来的。
    // 负控：把底座改成按 risk 判审批，或把两个 spec 的 RequiresApproval 改成随 risk 联动 → 红。
    [Fact]
    public void C3_风险等级不得决定审批()
    {
        Assert.Equal(RiskLevels.Medium, ErpActionSpecs.Draft.RiskLevel);
        Assert.False(ErpActionSpecs.Draft.RequiresApproval);

        Assert.Equal(RiskLevels.Medium, GiteaActionSpecs.IssueCreate.RiskLevel);
        Assert.True(GiteaActionSpecs.IssueCreate.RequiresApproval);
    }

    // C4：Gitea 域按业务对象命名能力，不按原语命名。
    // 负控：把 issue.create 改回 gitea.draft → 本条红。
    [Fact]
    public void C4_Gitea能力按业务对象命名_原语降级为属性()
    {
        var all = GiteaActionSpecs.All;
        Assert.Equal(4, all.Count);
        Assert.Equal(2, all.Select(s => s.ActionKey.Split('.')[0]).Distinct().Count());
        Assert.Contains(all, s => s.ActionKey == "repo.query");
        Assert.Contains(all, s => s.ActionKey == "issue.create");

        // 能力名不得等于原语名（erp.query / lab.submit 那种写法在 Gitea 域不允许）。
        foreach (var spec in all)
            Assert.DoesNotContain(ActionPrimitives.All,
                p => string.Equals(spec.ActionKey, "gitea." + p.ToLowerInvariant(), StringComparison.Ordinal));
    }

    // C5：AC-01 门禁 —— 新动作域只准引用 Core.Ports。
    // 负控：往 Gitea.csproj 里加任何一条非 Ports 的 ProjectReference → 本条红。
    [Fact]
    public void C5_新动作域只依赖CorePorts()
    {
        var root = ContractProbe.FindRepoRoot();
        var csproj = File.ReadAllText(
            Path.Combine(root, "src", "Domains", "Gitea", "AgentWorkbench.Gitea.csproj"));

        var refs = Regex.Matches(csproj, @"<ProjectReference\s+Include=""([^""]+)""");
        Assert.Single(refs);
        Assert.Contains("Core", refs[0].Groups[1].Value);
        Assert.Contains("Ports", refs[0].Groups[1].Value);
        Assert.DoesNotContain("ActionGateway", csproj);
        Assert.DoesNotContain("PackageReference", csproj);
    }

    // C6：审批与副作用真正解耦，且 ActionRunService 按 RequiresApproval 决策（行为断言，非源码字符串）。
    // 负控：把 ActionRunService 的挂起判断改回 spec.SideEffecting → 本条红（会错误挂起）。
    [Fact]
    public async Task C6_有副作用但免审批的动作不挂起()
    {
        var set = new TestActionSet();
        set.Registry.Spec = set.Spec with { SideEffecting = true, RequiresApproval = false };
        var (service, _, _) = TestHarness.Create(set);

        var result = await service.SubmitDeferredAsync(
            new ActionRequest("req-c6", "wang", "test.submit",
                JsonSerializer.SerializeToElement(new { }), "req-c6"),
            CancellationToken.None);

        Assert.NotEqual(ActionStatuses.Pending, result.Status);
        Assert.Equal(1, set.ExecutorStub.CallCount);
    }

    private static class RiskLevelValues
    {
        public static readonly string[] All =
            [RiskLevels.Low, RiskLevels.Medium, RiskLevels.High, RiskLevels.Critical];
    }
}

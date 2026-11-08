using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentWorkbench.ApplicationTests.Runtime;
using AgentWorkbench.Erp;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// AC-01 换域演练（设计 §5.3 / §8.1）+ 设计 §7 的四条依赖门禁。
/// 「底座 diff 为空」在测试里用 src/Core 全量源码哈希快照前后比对实现，等价于 git diff src/Core 为空，
/// 且不依赖测试机上有没有 git；人工复核仍可用 git diff src/Core。
/// </summary>
public sealed class DomainSwapAc01Tests
{
    private const string CoreDir = "src/Core";
    private const string DomainsDir = "src/Domains";
    private const string PortsDir = "src/Core/Ports";

    /// <summary>AC-02 禁词，取自 Py 仓 ADR 0017 §六；与 ContextBoundaryTests 的词表一致。</summary>
    private static readonly string[] BusinessForbiddenWords =
    {
        "PurchaseOrder", "Supplier", "AmountLimit", "TaxCode", "SAP",
        "用友", "金蝶", "Modbus", "ERPNext",
        "采购单", "供应商", "税码", "物料编码", "金额阈值", "成本口径",
        "月结期间", "审批层级", "单据状态码",
    };

    [Fact]
    public async Task AC01_DomainSwap_换域后底座源码diff为空()
    {
        // 负控（实证过）：把 LabActionSpecs.Submit 的 Primitive 改成 Query → 第二动作域跑不通，
        //   本条红（同批让 S6 一起红）。注意「往 src/Core 静态加一个文件」**不是**有效负控：
        //   本条比的是同一次运行内 before/after，两边都会含新文件 —— 那种变异会让本条保持绿，
        //   所以下面补了「快照扫描器能看见改动」的自证，证明快照不是恒真。
        var root = ContractProbe.FindRepoRoot();
        var coreDir = Path.Combine(root, CoreDir);
        var before = Snapshot(coreDir);
        Assert.NotEmpty(before);

        // 扫描器非恒真自证：临时往 src/Core 写一个探针文件，快照必须变；删掉后必须还原。
        var probePath = Path.Combine(coreDir, "__ac01_snapshot_probe.cs");
        Assert.False(File.Exists(probePath));
        try
        {
            File.WriteAllText(probePath, "// probe\n");
            Assert.NotEqual(before, Snapshot(coreDir));
        }
        finally
        {
            File.Delete(probePath);
        }
        Assert.Equal(before, Snapshot(coreDir));

        var erp = new ErpActionSet();
        var (erpService, _, _) = TestHarness.Create(erp);
        var erpResult = await erpService.RunAsync(
            TestRequests.Create(requestId: "req-swap-erp", actionKey: ErpActionSpecs.SubmitKey),
            CancellationToken.None);

        var lab = new FakeActionSet();
        var (labService, _, _) = TestHarness.Create(lab);
        var labResult = await labService.RunAsync(
            TestRequests.Create(
                requestId: "req-swap-lab",
                actionKey: LabActionSpecs.SubmitKey,
                argumentsJson: """{"lab_id":"LAB-B","slot_hours":3,"attendees":8}"""),
            CancellationToken.None);

        Assert.Equal(ActionStatuses.Succeeded, erpResult.Status);
        Assert.Equal(ActionStatuses.Succeeded, labResult.Status);
        Assert.StartsWith("PO-", erpResult.ExternalId);
        Assert.StartsWith("LB-", labResult.ExternalId);
        Assert.NotEqual(erp.Name, lab.Name);

        var after = Snapshot(coreDir);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Gate1_Core不得引用Domains()
    {
        // 负控：往任一 Core csproj 里加一行 <ProjectReference Include="..\Domains\..."> → 本条红。
        var root = ContractProbe.FindRepoRoot();
        var coreDir = Path.Combine(root, CoreDir);
        var csprojs = Directory.GetFiles(coreDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p)).ToArray();
        Assert.NotEmpty(csprojs);

        foreach (var csproj in csprojs)
        {
            Assert.DoesNotContain(ProjectReferences(csproj), r => r.Contains("Domains"));
        }

        var code = ReadCodeLines(Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p)));
        Assert.DoesNotContain("using AgentWorkbench.Erp", code);
        Assert.DoesNotContain("using AgentWorkbench.LabBooking", code);
    }

    [Fact]
    public void Gate2_Domains只允许引用CorePorts()
    {
        // 负控：往任一 Domains csproj 里加一条指向 Core/Runtime 的引用 → 本条红。
        var root = ContractProbe.FindRepoRoot();
        var csprojs = Directory.GetFiles(Path.Combine(root, DomainsDir), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p)).ToArray();
        Assert.True(csprojs.Length >= 2, "换域演练需要两个动作域工程");

        foreach (var csproj in csprojs)
        {
            var refs = ProjectReferences(csproj);
            Assert.NotEmpty(refs);
            Assert.All(refs, r => Assert.Contains("Core", r));
            Assert.All(refs, r => Assert.Contains("Ports", r));
        }
    }

    [Fact]
    public void Gate3_Ports源码无AC02业务禁词()
    {
        // 负控：在 Ports 任一 .cs 代码行里加一个 PurchaseOrder 标识符 → 本条红。
        var root = ContractProbe.FindRepoRoot();
        var portsDir = Path.Combine(root, PortsDir);
        var files = Directory.GetFiles(portsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p)).ToArray();
        Assert.NotEmpty(files);

        var code = ReadCodeLines(files);
        foreach (var word in BusinessForbiddenWords)
            Assert.DoesNotContain(word, code);
    }

    [Fact]
    public void Gate4_Ports的ProjectReference恒为零()
    {
        // 负控：往 Ports.csproj 加任意一条 ProjectReference → 本条红。
        var root = ContractProbe.FindRepoRoot();
        var csproj = Path.Combine(root, PortsDir, "AgentWorkbench.Ports.csproj");
        Assert.Empty(ProjectReferences(csproj));
    }

    private static bool IsGenerated(string path)
        => path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ProjectReferences(string csproj)
        => Regex.Matches(File.ReadAllText(csproj), @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

    private static string ReadCodeLines(IEnumerable<string> files)
        => string.Join("\n", files.Select(f => string.Join("\n",
            File.ReadAllLines(f).Where(line =>
            {
                var t = line.TrimStart();
                return !t.StartsWith("//", StringComparison.Ordinal)
                       && !t.StartsWith("/*", StringComparison.Ordinal)
                       && !t.StartsWith("*", StringComparison.Ordinal);
            }))));

    private static string Snapshot(string coreDir)
        => string.Join("\n", Directory
            .GetFiles(coreDir, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        && !IsGenerated(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(coreDir, p) + ":" +
                         Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
}

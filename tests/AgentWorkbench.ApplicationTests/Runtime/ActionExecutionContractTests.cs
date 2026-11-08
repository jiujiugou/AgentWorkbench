// 本组为契约存在性验收；所验能力当前未实现，预期全红，红是正确信号，实现落地后应逐条转绿。
// F5.4–F5.8：挂起恢复 / 幂等 / 超时只查证 / 审计五字段 / 假 ERP。
// 严禁引用任何仓内不存在的类型：这里只做「契约文件存在性 + 源码标记扫描」，
// 所有能力标记都是字符串字面量，不产生编译期符号引用，因此一定能编译。
// 扫描范围固定为 src/Core/Ports/ 与 src/Domains/Erp/ 下的 .cs 契约文件（当前均只有 .gitkeep、无任何 .cs），
// 刻意不扫 src/Core/Orchestration/AgentWorkbench.Workflow/ —— 那里已有 Workflow 层的 Checkpoint 字样，
// 扫进去会让 F5.4 误绿（规格 §5.3 明确：Workflow 层有、Harness/契约层无）。
// 共用的 ContractProbe 扫描器定义在 ActionContractExistenceTests.cs。
// 编号与负控见每条测试注释；每条测试都自带「扫描器非恒真」自证。

using System.IO;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

public sealed class ActionExecutionContractTests
{
    private const string Impossible = ContractProbe.ImpossibleToken;

    private static string Contracts(string root)
        => ContractProbe.ReadAllCs(root, ContractProbe.PortsDir, ContractProbe.ErpDir);

    // [F5.4] 挂起 → 人批 → 恢复，Harness run 层 checkpoint：契约里应留下 checkpoint 与恢复痕迹。
    // 负控：把契约里 checkpoint / 恢复（Resume/Suspend）相关能力删掉 → 两条断言红。
    [Fact]
    public void F54_挂起恢复与checkpoint契约应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = Contracts(root);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("SaveCheckpoint(); Resume(runId);", "Checkpoint"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：checkpoint 痕迹 + 恢复痕迹
        Assert.True(ContractProbe.FoundAny(contracts, "Checkpoint", "挂起"),
            "契约里应有 run 层 checkpoint 痕迹；当前零命中");
        Assert.True(ContractProbe.FoundAny(contracts, "Resume", "Suspend", "恢复"),
            "契约里应有挂起/恢复的痕迹；当前零命中");
    }

    // [F5.5] 幂等：同 request_id 只一次副作用，第二次返回首次结果。
    // 负控：删掉契约里的幂等能力（去重表 / 幂等键）或 request_id 去重键 → 对应断言红。
    [Fact]
    public void F55_幂等与request_id契约应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = Contracts(root);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("IdempotencyStore.Get(request_id);", "Idempot"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：幂等能力 + request_id 去重键
        Assert.True(ContractProbe.FoundAny(contracts, "Idempot", "幂等"),
            "契约里应有幂等能力；当前零命中");
        Assert.True(ContractProbe.FoundAny(contracts, "request_id", "RequestId"),
            "契约里应有 request_id 去重键；当前零命中");
    }

    // [F5.6] 超时只查证：记「执行中」并发起 Query，不重放写调用；查不到转人工。
    // 负控：删掉契约里超时 / 查证 Query / 执行中状态任一痕迹 → 对应断言红。
    [Fact]
    public void F56_超时只查证契约应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = Contracts(root);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("OnTimeout -> Query(InProgress)", "Timeout"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：超时 + 查证原语 + 执行中状态
        Assert.True(ContractProbe.FoundAny(contracts, "Timeout", "超时"),
            "契约里应有超时处置痕迹；当前零命中");
        Assert.True(ContractProbe.Found(contracts, "Query"),
            "契约里应有 Query 查证原语；当前零命中");
        Assert.True(ContractProbe.FoundAny(contracts, "InProgress", "Pending", "执行中"),
            "契约里应有「执行中」状态；当前零命中");
    }

    // [F5.7] 审计五字段：意图 / 参数 / 审批人 / 结果 / 外部单号，契约里应能查到五个字段名。
    // 负控：删掉五字段中任意一个（如删掉外部单号）→ 对应字段的断言红。
    [Fact]
    public void F57_审计五字段契约应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = Contracts(root);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("record Audit(Intent, Arguments, Approver, Result, ExternalId);", "Approver"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：五个字段各自留痕（同义词任一命中即可，避免实现者用词不同造成恒红）
        Assert.True(ContractProbe.FoundAny(contracts, "Intent", "意图"), "缺审计字段：意图");
        Assert.True(ContractProbe.FoundAny(contracts, "Arguments", "Parameters", "参数"), "缺审计字段：参数");
        Assert.True(ContractProbe.FoundAny(contracts, "Approver", "审批人"), "缺审计字段：审批人");
        Assert.True(ContractProbe.FoundAny(contracts, "Result", "结果"), "缺审计字段：结果");
        Assert.True(ContractProbe.FoundAny(contracts, "ExternalId", "ExternalOrder", "外部单号"),
            "缺审计字段：外部单号");
    }

    // [F5.8] 假 ERP：src/Domains/Erp/ 下应有四原语的内存实现（同时是单测装置与换域演练载体）。
    // 负控：把 src/Domains/Erp/ 下的 .cs 全删回只剩 .gitkeep、或删掉四原语 / 内存实现痕迹 → 断言红。
    [Fact]
    public void F58_假ERP四原语内存实现应存在()
    {
        var root = ContractProbe.FindRepoRoot();

        // —— 扫描器非恒真自证 ——
        // (1) 目录探测在稳定存在的目录上必须命中（证明仓库根定位与 Path.Combine 有效）
        Assert.True(Directory.Exists(Path.Combine(root, ContractProbe.ErpDir)));
        // (2) 枚举器在确实含 .cs 的目录上必须非空
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        // (3) Found 能命中确定存在的标记 / 对不可能标记恒假
        var erpText = ContractProbe.ReadAllCs(root, ContractProbe.ErpDir);
        Assert.True(ContractProbe.Found("class InMemoryFakeErp : Query, Draft, Submit, Cancel {}", "InMemory"));
        Assert.False(ContractProbe.Found(erpText, Impossible));

        // 红目标 1：Erp 域不能再只有 .gitkeep
        var erpCs = ContractProbe.EnumerateCsFiles(root, ContractProbe.ErpDir);
        Assert.True(erpCs.Count > 0,
            "src/Domains/Erp/ 当前只有 .gitkeep，没有任何假 ERP 实现 .cs 文件");

        // 红目标 2：内存 / 假实现痕迹
        Assert.True(ContractProbe.FoundAny(erpText, "InMemory", "Fake", "内存实现"),
            "假 ERP 应标明内存实现；当前零命中");

        // 红目标 3：四原语在 Erp 域里各自可跑
        foreach (var primitive in new[] { "Query", "Draft", "Submit", "Cancel" })
            Assert.True(ContractProbe.Found(erpText, primitive),
                $"假 ERP 里应有 {primitive} 原语；当前零命中");
    }
}

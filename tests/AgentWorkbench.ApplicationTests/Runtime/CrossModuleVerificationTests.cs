// 本组为契约存在性验收；所验能力当前未实现，预期全红，红是正确信号，实现落地后应逐条转绿。
// X1–X4：换域假动作集 / 无模型仍拒绝的验收用例 / 审计反查 / 无恒红恒真断言。
// 严禁引用任何仓内不存在的类型：只做「目录/文件存在性 + 源码标记扫描」，标记均为字符串字面量。
// 扫描 tests 源码时排除本组三个测试文件自身（它们只是「断言器」，不是「能力实现」），
// 否则本文件里出现的标记常量会把扫描器喂成恒真，这正是 X4 要禁止的情形。
// 共用的 ContractProbe 扫描器定义在 ActionContractExistenceTests.cs。
//
// 例外说明：X4 的判据是「tests/AgentWorkbench.Evaluation.AgentTask 目录存在 或 那条恒红断言已被处置」的 OR。
// 当前 tests/AgentWorkbench.ArchitectureTests/ArchitectureBudgets.cs（文档 §6 点名的恒红断言载体）
// 与整个评测工程目录均已不存在，恒红断言标识符已从测试源码消失，即「已被处置」，
// 故 X4 当前为绿——绿同样是「无恒红/恒真断言」的正确信号；X1–X3 仍预期红。详见输出说明。

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

public sealed class CrossModuleVerificationTests
{
    private const string Impossible = ContractProbe.ImpossibleToken;
    private const string EvalProjectRelativeDir = "tests/AgentWorkbench.Evaluation.AgentTask";
    private const string RedAssertionToken = "Trace_replay_must_be_consumed_by_evaluation_tests";

    /// <summary>本组三个测试文件自身；扫 tests 源码时排除，避免标记常量自污染。</summary>
    private static readonly string[] SelfFiles =
    {
        "ActionContractExistenceTests.cs",
        "ActionExecutionContractTests.cs",
        "CrossModuleVerificationTests.cs",
    };

    private static string ReadTestsSourceExceptSelf(string root)
        => string.Join("\n", ContractProbe.EnumerateCsFiles(root, ContractProbe.TestsDir)
            .Where(p => !SelfFiles.Any(f =>
                string.Equals(Path.GetFileName(p), f, StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText));

    // [X1] 换一套字段 / 策略 / 题库都不同的假动作集应存在，且「五项底座源码 diff 为空（AC-01）」可验。
    // 负控：把第二动作域的 .cs 全删（只留 Erp）、或删掉换域演练的验收用例痕迹 → 两条断言红。
    [Fact]
    public void X1_换域假动作集与底座diff可验痕迹应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var testsText = ReadTestsSourceExceptSelf(root);

        // —— 扫描器非恒真自证 ——
        // (1) 目录探测在稳定存在的目录上必须命中（证明仓库根定位有效，不是路径拼错导致的恒假）
        Assert.True(Directory.Exists(Path.Combine(root, ContractProbe.DomainsDir)));
        // (2) 枚举器在确实含 .cs 的目录上必须非空
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        // (3) 排除自身后仍读到了别的测试源码（证明过滤没把扫描器喂成空集）
        Assert.True(ContractProbe.Found(testsText, "ContextBoundaryTests"));
        // (4) Found 能命中确定存在的标记 / 对不可能标记恒假
        Assert.True(ContractProbe.Found("// AC-01 DomainSwap 换域演练", "AC-01"));
        Assert.False(ContractProbe.Found(testsText, Impossible));

        // 红目标 1：src/Domains/ 下至少要有两个含 .cs 的动作域（Erp + 另一套换域假动作集）
        var domainsRoot = Path.Combine(root, ContractProbe.DomainsDir);
        var domainDirsWithCs = Directory.Exists(domainsRoot)
            ? Directory.GetDirectories(domainsRoot)
                .Where(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories).Length > 0)
                .ToArray()
            : Array.Empty<string>();
        Assert.True(domainDirsWithCs.Length >= 2,
            $"换域演练需要第二套假动作集；当前含 .cs 的域目录数为 {domainDirsWithCs.Length}（src/Domains/ 只有 Erp 且仅 .gitkeep）");

        // 红目标 2：底座 diff 可验 —— 应存在换域演练的验收用例痕迹
        // （底座不被业务污染由 F3.7 结构断言守住；本条验「换域对比」这条组合验收确实有人跑）
        Assert.True(ContractProbe.FoundAny(testsText, "AC-01", "DomainSwap", "BaseDiff", "换域"),
            "缺少换域演练 / 底座 diff 对比的验收用例痕迹；当前零命中");
    }

    // [X2] 「拿掉模型、或让它吐畸形 JSON，系统仍拒绝且留痕」的验收用例应存在。
    // 负控：删掉这组验收用例（源码里不再有任何相关痕迹）→ 断言红。
    [Fact]
    public void X2_无模型仍拒绝的验收用例应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var testsText = ReadTestsSourceExceptSelf(root);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found(testsText, "ContextBoundaryTests")); // 排除自身后仍读到别的源码
        Assert.True(ContractProbe.Found("void NoModel_MalformedJson_StillRejects() {}", "NoModel"));
        Assert.False(ContractProbe.Found(testsText, Impossible));

        // 红目标：无模型 / 畸形 JSON 仍拒绝的验收用例痕迹
        Assert.True(ContractProbe.FoundAny(
                testsText, "无模型", "畸形 JSON", "NoModel", "MalformedJson", "malformed json"),
            "缺少「拿掉模型 / 畸形 JSON 仍拒绝且留痕」的验收用例；当前零命中");
    }

    // [X3] 「任取一笔执行，五个问题能答全」的审计反查能力应存在（Loop 终态 + Tools 轨迹 + 任务执行审计拼起来）。
    // 负控：删掉契约里的审计反查 / 回放能力痕迹 → 断言红。
    [Fact]
    public void X3_审计反查能力应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = ContractProbe.ReadAllCs(root, ContractProbe.PortsDir, ContractProbe.ErpDir);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("AuditTrail.Replay(traceId); // 反查", "反查"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：契约里应有审计反查 / 轨迹回放能力痕迹
        Assert.True(ContractProbe.FoundAny(
                contracts, "反查", "AuditQuery", "AuditLookup", "AuditTrail", "TraceReplay"),
            "缺少审计反查能力；当前契约目录为空，零命中");
    }

    // [X4] 验收「无恒红 / 恒真断言」：文档 §6 点名的恒红断言
    //      Trace_replay_must_be_consumed_by_evaluation_tests 所指的评测工程应存在，或该断言已被处置。
    // 负控（本条是「不存在某断言」的负向验证，负控取反向）：在任意测试源码里把被处置掉的
    //      Trace_replay_must_be_consumed_by_evaluation_tests 恒红断言原样塞回，且评测工程仍缺失
    //      → stillHasRedAssertion 变 true、OR 不成立 → 本条红。
    [Fact]
    public void X4_不应存在恒红断言_评测工程存在或断言已被处置()
    {
        var root = ContractProbe.FindRepoRoot();
        var testsText = ReadTestsSourceExceptSelf(root);

        // —— 扫描器非恒真自证 ——
        // (1) Directory.Exists 能命中确定存在的目录（证明根定位与路径拼接有效）
        Assert.True(Directory.Exists(Path.Combine(root, "tests/AgentWorkbench.ApplicationTests")));
        // (2) 枚举器在 tests 上确实读到了文件（非空集）
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.TestsDir).Count >= 5);
        // (3) 排除自身后仍读到了别的测试源码（证明「恒红断言零命中」不是把扫描器喂空造成的恒假）
        Assert.True(ContractProbe.Found(testsText, "ContextBoundaryTests"));
        // (4) Found 能命中该恒红断言标记 / 对不可能标记恒假
        Assert.True(ContractProbe.Found(
            "public void Trace_replay_must_be_consumed_by_evaluation_tests() { }",
            RedAssertionToken));
        Assert.False(ContractProbe.Found(testsText, Impossible));

        // 判据（OR）：评测工程建回来（断言可被满足），或恒红断言已从测试源码处置掉（不再恒红）。
        // 当前：ArchitectureBudgets.cs 与整个评测工程目录均已不存在，断言标识符在测试 .cs 里零命中
        //       → 「已被处置」成立 → 本条为绿；绿正是 X4「无恒红断言」的正确信号。
        var evalDirExists = Directory.Exists(Path.Combine(root, EvalProjectRelativeDir));
        var stillHasRedAssertion = ContractProbe.Found(testsText, RedAssertionToken);
        Assert.True(evalDirExists || !stillHasRedAssertion,
            $"{EvalProjectRelativeDir} 不存在（evalDirExists={evalDirExists}），且恒红断言仍在测试源码里"
            + $"（stillHasRedAssertion={stillHasRedAssertion}）——存在未处置的恒红断言");
    }
}

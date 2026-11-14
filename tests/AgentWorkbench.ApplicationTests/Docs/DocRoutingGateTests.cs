using System.Text;
using AgentWorkbench.ApplicationTests.Runtime;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Docs;

/// <summary>
/// 路由门禁（`docs/MAF六部件功能归属索引.md` §七）。
///
/// 防的断链：索引把一个词指到某个部件，但那个部件的文档里没有该词的字面出现——
/// 查词的人按指针进去 Ctrl+F 落空。这类缺口靠人撞见（如「工具调度」那次），撞不见就一直断着；
/// 门禁把「词表里每个词必须在目标文档里字面出现」变成会红的检查。
///
/// 两条：R0 证明检查器能认出断链（否则 R1 全绿也可能是假绿）；
/// R1 对真实词表逐条核对。词表是新词的唯一入口：新词先登记、登记即受门禁管。
/// </summary>
public sealed class DocRoutingGateTests
{
    private const string IndexRelPath = "docs/MAF六部件功能归属索引.md";

    // ── 检查器（纯函数，便于自证）────────────────────────────────

    /// <summary>从词表节解析出（词, 目标文档）对。只认「| 词 | src/ 路径 |」形状的行。</summary>
    private static List<(string Word, string TargetRel)> ParseRoutingTable(string indexText)
    {
        var entries = new List<(string, string)>();
        foreach (var raw in indexText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;

            var cells = line.Split('|');
            // "| 词 | 路径 |" → [ "", " 词 ", " 路径 ", "" ]
            if (cells.Length < 4) continue;

            var word = cells[1].Trim();
            var target = cells[2].Trim();
            if (word.Length == 0 || word == "词") continue;
            if (!target.StartsWith("docs/", StringComparison.Ordinal) &&
                !target.StartsWith("src/", StringComparison.Ordinal) ||
                !target.EndsWith(".md", StringComparison.Ordinal))
                continue;

            entries.Add((word, target));
        }
        return entries;
    }

    /// <summary>逐条核对：目标文档存在，且包含该词的字面。</summary>
    private static List<string> RoutingViolations(
        string repoRoot, IReadOnlyList<(string Word, string TargetRel)> entries)
    {
        var violations = new List<string>();
        foreach (var (word, target) in entries)
        {
            var full = Path.Combine(repoRoot, target.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                violations.Add($"「{word}」→ {target}（目标文档不存在）");
                continue;
            }
            var text = File.ReadAllText(full, Encoding.UTF8);
            if (!text.Contains(word, StringComparison.Ordinal))
                violations.Add($"「{word}」→ {target}（目标文档里没有该词的字面出现，指路链断）");
        }
        return violations;
    }

    // ── R0：门禁自身必须可被证伪 ────────────────────────────────

    [Fact]
    public void R0_检查器能认出伪造的断链与缺失目标()
    {
        var table = """
            # 前言，不含表格行

            | 词 | 目标文档 |
            | --- | --- |
            | 不存在的词 | src/Core/ __no_such__.md |
            | 工具调度 | src/Core/__no_such_dir__/__no_such__.md |
            """// 目标不存在 → 违规
            .Replace("__no_such_dir__", "no_such_dir", StringComparison.Ordinal)
            .Replace("__no_such__.md", "no_such.md", StringComparison.Ordinal);
        var entries = ParseRoutingTable(table);
        Assert.Equal(2, entries.Count);

        var violations = RoutingViolations(ContractProbe.FindRepoRoot(), entries);
        Assert.Equal(2, violations.Count);

        // 真实存在且包含词的条目不违规
        var ok = ParseRoutingTable("| 收口 | docs/core/循环驱动功能查询.md |");
        Assert.Empty(RoutingViolations(ContractProbe.FindRepoRoot(), ok));
    }

    [Fact]
    public void R0_词表解析认得出合法行并跳过表头与分隔行()
    {
        var table = """
            | 词 | 目标文档 |
            | --- | --- |
            | 工具调度 | docs/core/循环驱动功能查询.md |
            普通正文行 | 不是表格 | 不解析
            | 回填 | docs/README.md |
            """;
        var entries = ParseRoutingTable(table);
        // 只有以 "| " 开头的行参与；src/ 与 docs/ 路径都收（查询文档已迁 docs/）
        Assert.Equal(2, entries.Count);
        Assert.Equal("工具调度", entries[0].Word);
        Assert.EndsWith("循环驱动功能查询.md", entries[0].TargetRel, StringComparison.Ordinal);
        Assert.Equal("回填", entries[1].Word);
    }

    // ── R1：真实词表 ────────────────────────────────────────────

    [Fact]
    public void R1_索引路由词表里每个词必须在目标文档字面出现()
    {
        var index = File.ReadAllText(Path.Combine(ContractProbe.FindRepoRoot(), IndexRelPath), Encoding.UTF8);
        var entries = ParseRoutingTable(index);
        Assert.True(entries.Count > 0, "路由词表为空——门禁等于没挂，先把 §七 的词表补回来");

        var violations = RoutingViolations(ContractProbe.FindRepoRoot(), entries);
        Assert.True(violations.Count == 0,
            "指路链断了（词已登记、目标文档里却没有该词的字面；往目标文档补该词，或改登记）：\n" +
            string.Join("\n", violations));
    }
}

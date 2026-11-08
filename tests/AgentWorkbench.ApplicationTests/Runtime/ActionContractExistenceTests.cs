// 本组为契约存在性验收；所验能力当前未实现，预期全红，红是正确信号，实现落地后应逐条转绿。
// F5.1–F5.3：动作契约三类型 / SideEffecting / 四道校验 / 四原语。
// 严禁引用任何仓内不存在的类型（ActionSpec / ActionRequest / ActionResult / ActionGateway /
// PermissionQuery / 四原语 … 全部零实现）：这里只做「契约文件存在性 + 反射查类型名 + 源码标记扫描」，
// 被查的类型名与能力标记一律是字符串字面量，不产生任何编译期符号引用，因此一定能编译。
// 编号与负控见每条测试注释；每条测试都自带「扫描器非恒真」自证。
// 复用说明：本文件末的 ContractProbe 为本组三个文件共用的扫描器，另两个文件直接引用它。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// 三个契约存在性测试文件共用的扫描器。
/// 全部使用 .NET / xunit 标准 API；路径一律由 FindRepoRoot 定位仓库根后拼相对路径，不硬编码盘符。
/// </summary>
internal static class ContractProbe
{
    public const string PortsDir = "src/Core/Ports";
    public const string ErpDir = "src/Domains/Erp";
    public const string DomainsDir = "src/Domains";
    public const string TestsDir = "tests";
    public const string SelfProbeDir = "tests/AgentWorkbench.ApplicationTests/Runtime";

    /// <summary>绝不可能出现在契约源码里的标记，用于「扫描器不恒真」的反例自证。</summary>
    public const string ImpossibleToken = "__CONTRACT_PROBE_NEVER_PRESENT_TOKEN_F5X4__";

    /// <summary>
    /// 从 AppContext.BaseDirectory 起逐级取 DirectoryInfo.Parent，向上查找 AgentWorkbench.slnx；
    /// 再以 Directory.GetCurrentDirectory() 作兜底起点。返回 slnx 所在目录（仓库根）。
    /// </summary>
    public static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("未找到 AgentWorkbench.slnx，无法定位仓库根。");
    }

    /// <summary>列出某相对目录下（含子目录）的全部 .cs 文件；目录不存在时返回空集合而非抛异常。</summary>
    public static IReadOnlyList<string> EnumerateCsFiles(string root, string relativeDir)
    {
        var dir = Path.Combine(root, relativeDir);
        if (!Directory.Exists(dir))
            return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
    }

    /// <summary>把若干相对目录下的全部 .cs 文本拼成一份大文本，供标记扫描。</summary>
    public static string ReadAllCs(string root, params string[] relativeDirs)
        => string.Join("\n", relativeDirs
            .SelectMany(d => EnumerateCsFiles(root, d))
            .Select(File.ReadAllText));

    /// <summary>大小写不敏感的子串命中判定。</summary>
    public static bool Found(string text, string token)
        => !string.IsNullOrEmpty(text)
           && !string.IsNullOrEmpty(token)
           && text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>一组同义标记命中任意一个即为真。</summary>
    public static bool FoundAny(string text, params string[] tokens)
        => tokens.Any(t => Found(text, t));

    /// <summary>
    /// 反射：枚举 AppDomain 已加载程序集的类型名。
    /// agentWorkbenchOnly=true 时只看简单名以 "AgentWorkbench" 开头的程序集。
    /// 显式处理 ReflectionTypeLoadException（取回能加载的部分类型），其余加载异常跳过该程序集。
    /// </summary>
    public static IReadOnlyList<string> LoadedTypeNames(bool agentWorkbenchOnly = false)
    {
        var names = new List<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (agentWorkbenchOnly &&
                !(asm.GetName().Name?.StartsWith("AgentWorkbench", StringComparison.Ordinal) ?? false))
                continue;

            IEnumerable<Type> types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Select(t => t!);
            }
            catch
            {
                continue; // 个别程序集无法反射时不影响整体扫描
            }

            foreach (var t in types)
                names.Add(t.Name);
        }
        return names;
    }
}

public sealed class ActionContractExistenceTests
{
    private const string Impossible = ContractProbe.ImpossibleToken;

    // [F5.1] 动作契约：ActionSpec / ActionRequest / ActionResult 三类型 + SideEffecting 标志应存在。
    // 负控：把 src/Core/Ports/ 下定义三类型的契约文件删掉、或删掉类型里的 SideEffecting 标志
    //       → 「契约文件数 > 0」/ 反射三类型名 / SideEffecting 标记断言会红。
    [Fact]
    public void F51_动作契约三类型与SideEffecting标志应存在()
    {
        var root = ContractProbe.FindRepoRoot();

        // —— 扫描器非恒真自证 ——
        // (1) 枚举器在确实含 .cs 的目录上必须非空，证明「契约文件数为 0」不是枚举器坏掉造成的恒真
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2,
            "扫描器应能在测试项目 Runtime 目录枚举到 ≥2 个 .cs（证明枚举器能命中）");
        // (2) Found 能命中确定存在的标记
        Assert.True(ContractProbe.Found("public sealed record ActionSpec(string Args);", "ActionSpec"));
        // (3) Found 对绝不可能出现的标记恒为假
        Assert.False(ContractProbe.Found(ContractProbe.ReadAllCs(root, ContractProbe.PortsDir), Impossible));

        // 反射扫描器自证：全量集合里 String（框架类型）与本程序集的 ContractProbe 必命中；
        // 再对一个绝不会被加载的类型名断言恒假；过滤后的 AgentWorkbench.* 集合必须非空。
        var allTypes = ContractProbe.LoadedTypeNames();
        var awTypes = ContractProbe.LoadedTypeNames(agentWorkbenchOnly: true);
        Assert.Contains("String", allTypes);
        Assert.Contains("ContractProbe", allTypes);
        Assert.DoesNotContain("__NEVER_LOADED_TYPE_F51__", allTypes);
        Assert.NotEmpty(awTypes);

        // 红目标 1：src/Core/Ports/ 不能再只有 .gitkeep，必须至少有一个 .cs 契约文件
        var portsCs = ContractProbe.EnumerateCsFiles(root, ContractProbe.PortsDir);
        Assert.True(portsCs.Count > 0,
            "src/Core/Ports/ 当前只有 .gitkeep，没有任何动作契约 .cs 文件");

        // 红目标 2：反射在已加载的 AgentWorkbench.* 程序集里应能查到三个契约类型名
        foreach (var typeName in new[] { "ActionSpec", "ActionRequest", "ActionResult" })
            Assert.Contains(typeName, awTypes);

        // 红目标 3：契约源码里应有 SideEffecting 标志（Draft 标可重复 / Submit 标必登记）
        var contracts = ContractProbe.ReadAllCs(root, ContractProbe.PortsDir);
        Assert.True(ContractProbe.Found(contracts, "SideEffecting"),
            "契约里应有 SideEffecting 标志；当前契约目录为空，标记零命中");
    }

    // [F5.2] 四道校验：schema → 权限 → 业务规则 → 审计，契约标记应全部存在。
    // 负控：删掉任一道校验的契约痕迹（例如去掉权限校验或审计校验的定义/标记）→ 对应断言红。
    [Fact]
    public void F52_四道校验契约应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = ContractProbe.ReadAllCs(root, ContractProbe.PortsDir, ContractProbe.ErpDir);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("ValidateSchema(Permission p); // BusinessRule + Audit", "Schema"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：四道各留一个能力标记（同义词任一命中即可，避免实现者用词不同造成恒红）
        Assert.True(ContractProbe.FoundAny(contracts, "Schema", "JsonSchema"),
            "缺少 schema 校验痕迹");
        Assert.True(ContractProbe.FoundAny(contracts, "Permission", "权限"),
            "缺少权限校验痕迹");
        Assert.True(ContractProbe.FoundAny(contracts, "BusinessRule", "Business Rule", "业务规则"),
            "缺少业务规则校验痕迹");
        Assert.True(ContractProbe.FoundAny(contracts, "Audit", "审计"),
            "缺少审计校验痕迹");
    }

    // [F5.3] 四原语：Query / Draft / Submit / Cancel，契约标记应全部存在。
    // 负控：删掉任一原语的定义/标记（如去掉 Cancel）→ 对应断言红。
    [Fact]
    public void F53_四原语契约应存在()
    {
        var root = ContractProbe.FindRepoRoot();
        var contracts = ContractProbe.ReadAllCs(root, ContractProbe.PortsDir, ContractProbe.ErpDir);

        // —— 扫描器非恒真自证 ——
        Assert.True(ContractProbe.EnumerateCsFiles(root, ContractProbe.SelfProbeDir).Count >= 2);
        Assert.True(ContractProbe.Found("Query Draft Submit Cancel", "Draft"));
        Assert.False(ContractProbe.Found(contracts, Impossible));

        // 红目标：四个原语各自留痕；当前契约目录为空，四个全部零命中
        foreach (var primitive in new[] { "Query", "Draft", "Submit", "Cancel" })
            Assert.True(ContractProbe.Found(contracts, primitive),
                $"契约里应有 {primitive} 原语；当前零命中");
    }
}

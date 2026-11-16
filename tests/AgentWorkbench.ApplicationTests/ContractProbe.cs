// 测试共用的仓库根定位与源码扫描器。
// 原本寄生在「契约存在性验收」那三个文件里；那些文件已删（它们扫源码标记、不断言行为），
// 但 ContractProbe 本身仍被文档门禁与 AC-01 换域验收使用，故独立成文件。
// 路径一律由 FindRepoRoot 定位仓库根后拼相对路径，不硬编码盘符。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AgentWorkbench.ApplicationTests;

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

    /// <summary>
    /// 把输出目录下所有 AgentWorkbench.*.dll 加载进当前 AppDomain。
    /// 只加载已被编译输出的程序集，不按名字凭空构造类型；加载失败一律跳过，
    /// 因此「契约不存在」时不会凭空造出类型名，扫描器仍保持区分度。
    /// </summary>
    private static int _assembliesEnsured;

    private static void EnsureAgentWorkbenchAssembliesLoaded()
    {
        if (Interlocked.Exchange(ref _assembliesEnsured, 1) != 0)
            return;

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "AgentWorkbench.*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(dll);
            if (loaded.Contains(simpleName))
                continue;
            try
            {
                Assembly.LoadFrom(dll);
            }
            catch (BadImageFormatException)
            {
                // 原生 dll 或资源程序集，跳过
            }
            catch (FileLoadException)
            {
                // 已加载或版本冲突，跳过
            }
        }
    }

    /// <summary>列出某相对目录下（含子目录）的全部 .cs 文件；目录不存在时返回空集合而非抛异常。</summary>
    public static IReadOnlyList<string> EnumerateCsFiles(string root, string relativeDir)
    {
        var dir = Path.Combine(root, relativeDir);
        if (!Directory.Exists(dir))
            return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
    }

    /// <summary>
    /// 把若干相对目录下的全部 .cs 文本拼成一份大文本，供标记扫描。
    ///
    /// 2026-11-08 修正假绿：默认剥掉纯注释行。契约的 XML 文档会反复提到
    /// SideEffecting / 挂起 / 恢复 / 审计等词，若把它们当作「能力存在」的证据，
    /// 删掉真实参数后测试照样绿 —— 这是负控实证抓到的假绿。
    /// 与 AC-02「注释不算违规」对称：注释也不算能力证据，标记必须由真实代码承载。
    /// </summary>
    public static string ReadAllCs(string root, params string[] relativeDirs)
        => string.Join("\n", relativeDirs
            .SelectMany(d => EnumerateCsFiles(root, d))
            .Select(f => StripComments(File.ReadAllText(f))));

    /// <summary>去掉纯注释行（//、///、/*、* 开头的行），只留代码行。</summary>
    private static string StripComments(string source)
    {
        var code = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) ||
                t.StartsWith("/*", StringComparison.Ordinal) ||
                t.StartsWith("*", StringComparison.Ordinal))
                continue;
            code.Add(line);
        }
        return string.Join("\n", code);
    }

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
    ///
    /// 2026-11-08 修正装置盲区：Core.Ports 是纯契约工程，没有任何运行时代码引用它的类型，
    /// CLR 不会主动加载，「只扫已加载程序集」会让真实存在的契约查不到（F5.1 假红）。
    /// 因此扫描前先把输出目录下的 AgentWorkbench.*.dll 全部加载进来。
    /// 这修的是扫描前提，不是断言：删掉任一契约类型，F5.1 仍会红。
    /// </summary>
    public static IReadOnlyList<string> LoadedTypeNames(bool agentWorkbenchOnly = false)
    {
        EnsureAgentWorkbenchAssembliesLoaded();

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

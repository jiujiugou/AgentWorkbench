namespace AgentWorkbench.Memory;

/// <summary>记忆条目类别取值（与 <c>memory_items.kind</c> 对应）。MVP 只用来标注与过滤，不改变召回算法。</summary>
public static class MemoryKinds
{
    public const string Fact = "fact";
    public const string Preference = "preference";
    public const string Decision = "decision";
    public const string Note = "note";

    /// <summary>内置类别（工具描述用；写入时不做白名单校验，允许调用方自定义）。</summary>
    public static readonly IReadOnlyList<string> All = [Fact, Preference, Decision, Note];

    /// <summary>空值回落到 <see cref="Fact"/>，其余去空白转小写，避免 <c>"Fact"</c> / <c>"fact "</c> 被当成两个类别。</summary>
    public static string Normalize(string? kind)
        => string.IsNullOrWhiteSpace(kind) ? Fact : kind.Trim().ToLowerInvariant();
}

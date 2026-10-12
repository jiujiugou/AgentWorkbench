namespace AgentWorkbench.Memory.Persistence.Tools;

/// <summary>
/// 记忆工具的作用域解析：模型只能选「本作用域 / 全局」这种粗粒度开关，
/// 具体 scope_id（会话 id）由编排层设置的 ambient 作用域决定，模型无法指定别人的 id。
/// </summary>
internal static class MemoryToolScope
{
    /// <summary>
    /// 解析写入/召回作用域。
    /// </summary>
    /// <param name="global">工具参数 <c>global=true</c>：显式写/查全局记忆。</param>
    /// <returns>无法确定作用域（既没要全局、又没有 ambient 会话）时返回 <c>null</c>。</returns>
    public static MemoryScope? Resolve(IMemoryScopeAccessor accessor, MemoryOptions options, bool global)
    {
        if (global)
            return MemoryScope.Global;

        if (accessor.Current is { } ambient)
            return ambient;

        // 不在任何 run 内（例如被直接调用）：只有配置明确指向 global 时才降级，否则宁可拒绝也不乱写。
        return options.LongTermScope == MemoryScopeTypes.Global ? MemoryScope.Global : null;
    }
}

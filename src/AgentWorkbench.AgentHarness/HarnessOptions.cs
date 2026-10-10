namespace AgentWorkbench.AgentHarness;

/// <summary>Agent Harness（运行时横切轴）配置。</summary>
public sealed class HarnessOptions
{
    public const string SectionName = "Harness";

    public LoopOptions Loop { get; set; } = new();

    public ContextOptions Context { get; set; } = new();

    public MemoryOptions Memory { get; set; } = new();

    public ToolOptions Tools { get; set; } = new();

    public sealed class LoopOptions
    {
        /// <summary>单次 run 的最大迭代步数（= 工具环上限）。</summary>
        public int MaxIterations { get; set; } = 6;

        /// <summary>单次 run 超时（秒）。</summary>
        public int TimeoutSeconds { get; set; } = 30;
    }

    public sealed class ContextOptions
    {
        /// <summary>是否启用检索增强上下文（RAG 注入）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>注入的检索片段数量。</summary>
        public int RagTopK { get; set; } = 5;
    }

    public sealed class MemoryOptions
    {
        /// <summary>是否维持会话内记忆（同一 sessionId 多轮历史）。</summary>
        public bool SessionEnabled { get; set; } = true;
    }

    public sealed class ToolOptions
    {
        /// <summary>工具白名单（不在其中不注册、不可调用）。</summary>
        public List<string> Whitelist { get; set; } =
        [
            "get_current_time",
            "calculator",
            "search_knowledge",
            "list_documents"
        ];
    }
}

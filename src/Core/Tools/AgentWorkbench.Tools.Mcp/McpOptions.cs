namespace AgentWorkbench.Tools.Mcp;

/// <summary>MCP 接入配置：一份 server 清单，每项描述怎么连、连上后放行哪些工具。</summary>
/// <remarks>
/// 默认关闭。MCP server 是一个外部进程或外部 HTTP 端点，启动期连不上不该拖垮主进程——
/// 但也不能「连不上就当没有工具」而让上层以为能力还在，所以连不上时记 Error 且该 server 零工具。
/// </remarks>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>总开关。false 时一个 server 都不连，工具目录里也不会出现任何 mcp__ 开头的名字。</summary>
    public bool Enabled { get; set; }

    public List<McpServerOptions> Servers { get; set; } = [];

    /// <summary>工具名统一前缀：<c>mcp__{server}__{tool}</c>。</summary>
    /// <remarks>
    /// 加前缀不是为了好看，是因为三件事都依赖它：
    /// ① 白名单能按 server 整段放行（<c>mcp__erpnext__*</c>）；
    /// ② 轨迹与审批里一眼看出「这一步碰的是外部系统」；
    /// ③ 两个 server 有同名工具时不会在 ToolCatalog 的去重里被悄悄丢掉一个。
    /// </remarks>
    public const string ToolNamePrefix = "mcp__";
}

/// <summary>一个 MCP server 的连接参数。</summary>
public sealed class McpServerOptions
{
    /// <summary>用于工具名前缀，必须唯一且只含字母数字下划线。</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary><c>stdio</c>（本地子进程）或 <c>http</c>（远端 Streamable HTTP）。</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>stdio：可执行文件。<c>npx</c> / <c>node</c> / 自带二进制的绝对路径都行。</summary>
    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    /// <summary>stdio：额外环境变量（API Key 之类）。子进程默认不继承父进程环境，要继承把 InheritEnvironment 打开。</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    public bool InheritEnvironment { get; set; } = true;

    /// <summary>http：server 的 MCP 端点，例如 <c>http://localhost:3001/</c>。</summary>
    public string? Endpoint { get; set; }

    /// <summary>http：附加请求头（Authorization 等）。</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>
    /// 只暴露这些工具（用 server 的原名，不含前缀）。留空 = 该 server 的所有工具都暴露，
    /// 但**仍然要过 <c>Harness:Tools:Whitelist</c>**：两道关，缺一不可。
    /// </summary>
    public List<string> AllowTools { get; set; } = [];
}

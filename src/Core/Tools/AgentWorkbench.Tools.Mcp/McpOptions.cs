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
    /// 放行这些工具（用 server 的原名，不含前缀）。<b>留空 = 一个都不放行。</b>
    /// </summary>
    /// <remarks>
    /// 默认是空集，不是「全部」。「这个 server 装了哪些工具」是别人决定的，
    /// 不写清楚就等于把别人的能力清单当成我们的授权清单 —— 而没被点名的工具，
    /// 在日志和排障里都不会留下任何痕迹。
    /// <para>
    /// 放行之后仍要过两道：本 server 的能力准入（见 <see cref="McpToolCapability"/>）
    /// 与 <c>Harness:Tools:Whitelist</c>。三道关缺一不可。
    /// </para>
    /// </remarks>
    public List<string> AllowTools { get; set; } = [];

    /// <summary>
    /// 显式信任为「只读」的工具名（server 原名，不含前缀）。
    /// </summary>
    /// <remarks>
    /// 只在 server 自己<b>没有</b>声明 <c>ReadOnlyHint</c> 时才需要它。
    /// 我们不猜工具语义，但可以为自己写的、或已经审过的 server 逐个点名。
    /// 名字必须写全，<b>不支持通配</b>：一条 <c>*</c> 就能让这条配置读完等于没读。
    /// </remarks>
    public List<string> TrustedReadOnly { get; set; } = [];

    /// <summary>
    /// 这个 server 的工具能否直通模型目录。默认 <c>true</c>（只读的可以直通）。
    /// </summary>
    /// <remarks>
    /// 设为 <c>false</c> 时，它的全部工具一律只能由动作闸背后的执行体调用 ——
    /// 用于「所有能力都必须过四道闸」的外部系统（ERP / 工单 / 财务这类）。
    /// 与 <c>ReadOnlyHint</c> 无关：即使工具自称只读，也不给模型直通，
    /// 因为「谁说它只读」这件事对这类系统不能由 server 单方面决定。
    /// </remarks>
    public bool ExposeToModel { get; set; } = true;
}

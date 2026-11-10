# AgentWorkbench.Tools.Mcp

把外部 MCP server 的工具接进工具目录。**只做接入，不做治理**——白名单与闸门仍在原来的位置。

## 它做什么

启动时按 `Mcp:Servers` 连一遍所有启用的 server，`ListToolsAsync()` 拉工具，给每个工具名加上
`mcp__{server}__` 前缀后放进 `McpToolRegistry`。`McpToolProvider` 把这份快照交给 `ToolCatalog`，
再经 `Harness:Tools:Whitelist` 裁剪一次，最后进 `ChatOptions.Tools`。

```
Program.cs  AddNitroMcp
   └─ McpConnectionHostedService (启动期)
        └─ McpToolRegistry  ──  McpClient ──▶ 外部 server（stdio 子进程 / HTTP）
              └─ McpToolProvider : IAgentToolProvider
                    └─ ToolCatalog（白名单裁剪）
                          └─ ChatOptions.Tools（模型可见）
```

## 为什么是启动期，不是热重载

`IAgentToolProvider.GetTools()` 是同步签名，MCP 建连是异步的。在同步方法里等异步握手会让每次 run
都多一次建连，还可能死锁。所以连接放在 `IHostedService`，工具目录只读快照。

代价：改了 `Mcp:Servers` 要重启进程。白名单仍是热重载的（`IOptionsMonitor`），因为它是安全边界，
改它必须是显式动作，且下个 run 立即生效。

## 配置

```json
"Mcp": {
  "Enabled": true,
  "Servers": [
    {
      "Name": "everything",
      "Transport": "stdio",
      "Command": "npx",
      "Arguments": [ "-y", "@modelcontextprotocol/server-everything" ],
      "InheritEnvironment": true,
      "AllowTools": []
    },
    {
      "Name": "erpnext",
      "Transport": "stdio",
      "Command": "frappe-mcp-server-stdio",
      "Arguments": [ "--config", "/etc/erpnext-mcp.yaml" ],
      "Environment": { "ERPNEXT_API_KEY": "...", "ERPNEXT_API_SECRET": "..." },
      "AllowTools": [ "get_document", "list_documents", "get_doctype_meta" ]
    }
  ]
}
```

HTTP 形态把 `Transport` 换成 `http` 并给 `Endpoint`：

```json
{ "Name": "remote", "Transport": "http", "Endpoint": "https://mcp.example.com/",
  "Headers": { "Authorization": "Bearer ..." } }
```

**两道关，缺一不可**：

1. `Mcp:Servers[*].AllowTools` —— 该 server 拉哪些工具（空 = 全拉）。
2. `Harness:Tools:Whitelist` —— 模型能看见哪些。MCP 工具要写 `mcp__{server}__*`
   （尾部通配），或逐个写全名。

只写第 1 条不写第 2 条，工具连上了但模型看不见；这是刻意的默认拒绝。

## 已证

- 2026-11-10：`@modelcontextprotocol/server-everything` 连上，13/13 个工具入目录。
- 同一轮真模型（`glm-4-flash-250414`）调用 `mcp__everything__echo`，入参 `{"message":"hello-mcp"}`，
  返回 `Echo: hello-mcp`，run `tools=1 / status=succeeded`。

## 已知风险（不是待办，是接入的代价）

**MCP 工具是「直调」的，不经过 `propose_action`。** 一旦放行一个会写外部系统的工具
（`create_document` / `delete_document`），模型就能在四道闸门之外改动业务数据，
`action_trace` 里也不会留下可追的动作链。

因此本项目的用法约定：

- **只读工具**（`get_document` / `list_documents` / `get_doctype_meta`）可以直接接；
- **写工具**不进白名单。要写，走 `Domains/Erp` 的动作规格 + `propose_action`，
  让审批与审计留在链路上。

这条约定目前靠配置，不靠代码——`AllowTools` 里写一个 `create_document` 是拦不住的。
要变成硬约束，得在 `ToolCatalog` 增加「副作用声明」维度，按 `AITool` 的来源拒绝写类工具。
在那之前，改白名单的人必须知道自己在开什么。

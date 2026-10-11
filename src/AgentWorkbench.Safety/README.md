# AgentWorkbench.Safety

可靠性与安全模块（功能域 5）：当前落地的是**工具授权白名单** —— 白名单外的工具「模型看不到、也调不动」。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ToolPolicy.cs` | `IToolPolicy`：`Whitelist` 快照、`IsAllowed(name)`、`Allowed(names)` |
| `ToolPolicyOptions.cs` | 配置节 `Harness:Tools`：`Whitelist` 默认 `get_current_time` / `calculator` / `search_knowledge` / `list_documents` |
| `SafetyServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroSafety` |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IToolPolicy` | `Whitelist`、`IsAllowed`、`Allowed` | `ToolPolicy` | `ToolExecution.ToolCatalog`（过滤后才注册给模型）、`HarnessCapabilities.Tools` |

> 白名单在**目录层**生效：不在白名单的工具不会出现在模型的工具列表里，因此不是「调用时才拒绝」。

> 未落地：敏感操作确认、执行沙箱、审计与资源限制 —— 见 `docs/底座建设清单.md` 缺口清单。

## 依赖

Shared。

# AgentWorkbench.Safety

可靠性与安全模块（功能域 5）：落地**两个正交的维度** ——

1. **能力白名单**（`IToolPolicy`）：决定某个工具**能不能用**；白名单外「模型看不到、也调不动」。
2. **审批级别**（`IToolApprovalPolicy`）：决定可见的工具**执行前要不要先问人**（`Auto` / `Ask` / `Deny`）。

> 两维**并列且正交，绝不合并**。「可见也允许，但每次都要确认」与「可见也允许，且不问」
> 是两种真实状态，揉进一个接口就再也表达不出来 —— 这是把沙箱轴与审批轴拆开的意义。
> 唯一的组合点是 `ToolExecutionPipeline`：它先拿白名单裁剪工具，再在每次调用前问审批级别。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ToolPolicy.cs` | `IToolPolicy`：`Whitelist`、`IsAllowed(name)`、`Allowed(names)` |
| `ToolPolicyOptions.cs` | 配置节 `Harness:Tools`：`Whitelist` 默认 `get_current_time` / `calculator` / `search_knowledge` / `list_documents` / `remember` / `recall` |
| `ToolApproval.cs` | `IToolApprovalPolicy` + `ToolApprovalPolicy` + `ToolApprovalOptions` + `AutoToolApprovalPolicy` |
| `SafetyServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroSafety`（两维各绑一条配置节） |

> `remember` / `recall` 这两个工具由 `Memory.Persistence` 提供；未装该模块时白名单里有名字但没有实现，
> 等于模型看不到 —— 白名单是「允许」，不是「存在」。

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IToolPolicy` | `Whitelist`、`IsAllowed`、`Allowed` | `ToolPolicy` | `ToolExecution.ToolCatalog`（过滤后才注册给模型）、`HarnessCapabilities.Tools` |
| `IToolApprovalPolicy` | `Decision(toolName)` | `ToolApprovalPolicy`（`AutoToolApprovalPolicy` 为显式全放行） | `ToolExecution.ToolExecutionPipeline`（每次工具调用前过闸门） |

> 白名单在**目录层**生效：不在白名单的工具不会出现在模型的工具列表里，因此不是「调用时才拒绝」。
> 审批在**执行层**生效：工具已可见、调用已发出，闸门判 `Auto` 直行 / `Deny` 拒绝 / `Ask` 挂起问人。

## 配置与热重载

两维都持 `IOptionsMonitor`，改 `appsettings.json` 保存即热重载、**下个 run 生效，零停服**：

```json
"Harness": {
  "Tools":    { "Whitelist": ["get_current_time", "calculator"] },
  "Approval": { "Default": "Auto", "Overrides": { "shell": "Deny" } }
}
```

`Default` 是 `Auto`（**不显式提升就永不打扰用户**）；`Overrides` 按工具名逐个提升。
提升为 `Ask` 时还要调用方提供审批通道（`IToolApprover`）——没有通道，`Ask` 一律按拒绝处理（fail-closed）。

> 未落地：执行沙箱（能碰哪些路径 / 多久超时 / 输出多大）、结构化审计留存 —— 见 `docs/底座建设清单.md` 缺口清单。

## 依赖

Shared。

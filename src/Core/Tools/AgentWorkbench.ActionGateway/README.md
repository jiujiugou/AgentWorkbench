# ActionGateway — M1 受控动作内核（底座侧）

- **依据**：`docs/设计/M1-受控动作执行流程设计.md` §2.2 / §2.4 / §4 / §6 / §7。
- **依赖**：只引用 `Core.Ports`。**不得引用 `Domains`**（§7 门禁 1），不得出现业务词（AC-02）。换域时本工程一行不改（AC-01）。

## 装了什么

| 文件 | 职责 |
|---|---|
| `FailClosedActionGateway.cs` | 四道校验：Schema → Permission → BusinessRule → Audit，**严格按序、任一不过即拒绝**。通过只返回 `Status=Validated`，不调用执行体 |
| `DefaultDenyComponents.cs` | 零配置的默认全拒绝实现；`FailClosedDefaults.Create()` 不提供任何放行默认 |
| `ActionRunService.cs` | 六步链编排：四道校验 → 挂起审批（仅 `SideEffecting`）→ 幂等领取 → 执行 → 超时只查证 → 收口 |
| `InMemoryActionRunStore.cs` | run 状态、Checkpoint（只存执行进度）、按 `request_id` 的去重表、终态缓存 |
| `InMemoryTraceStore.cs` | `ITraceSink` + `IAuditQuery` 的内存实现；写失败即拒绝 |
| `TraceEvents.cs` | `TraceEvent` 统一构造点；审计五字段由流程各步产出 |
| `ActionResults.cs` | 底座侧 `ActionResult` 构造助手 |

## 三条不能破的顺序

1. **校验未过 / 审批未通过 → `IActionExecutor` 零调用**。网关只出「校验结论」，执行由编排层在审批之后发起，所以「零调用」是可被计数断言证伪的行为，不是结构上恒真。
2. **超时 / `Unknown` → 只发 `Query` 查证，绝不重放写动作**。查证用的是 `IActionSet.QuerySpec`，不是原 `spec`。
3. **轨迹写不进去 → 拒绝**，不静默放行。四道每道各写一条阶段轨迹，第 4 道显式确认通道可写。

## 模型在哪

`ActionRunService` 的输入是**结构化 `ActionRequest`**，不依赖任何 `IChatClient`。模型只负责把自然语言翻成这个结构；拿掉模型、或让它吐畸形 JSON，`FailClosedDefaults.Create()` 仍然拒绝且留痕。这是「拿掉模型，系统还能安全地拒绝」的落点，不是靠提示词保证。

## 没做什么

真 ERPNext、真实权限四维、真实制度检索、最小审批页、独立 `ArchitectureTests` 工程 —— 分别是 M2 / M3 / 待用户裁决（M0 报告 L2）。四条依赖门禁暂落在 `ApplicationTests/ActionGateway/DomainSwapAc01Tests.cs`。

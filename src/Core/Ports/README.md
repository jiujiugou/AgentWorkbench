# Core.Ports — 底座与动作域之间唯一的契约边界

- **状态**：M1 第一步产出（2026-11-08）。依据 `docs/设计/M1-受控动作执行流程设计.md` §3 / §4 / §7。
- **性质**：只有接口与 record，**零实现、零业务词、零 NuGet 包、零 ProjectReference**。`Domains/*` 唯一允许 import 的工程。

## 装了什么

| 文件 | 内容 |
|---|---|
| `ActionContracts.cs` | `ActionSpec` / `ActionRequest` / `ActionResult` 三类型；`ActionPrimitives` 四原语；`ActionStatuses` 终态字符串 |
| `PortInterfaces.cs` | 四道校验接口（Schema / Permission / BusinessRule / Audit）、`IActionGateway` / `IActionExecutor` / `IActionSpecRegistry`、各类判定 record、`EvidenceRef` |
| `TraceEvent.cs` | `TraceEvent`（审计五字段 + Stage + Outcome + 时间）、`ErrorEnvelope` 类型名占位 |
| `GlobalUsings.cs` | 仅 `System.Text.Json`（框架自带） |

## 三条不能破的约束

1. **零依赖**：不引用 `AgentWorkbench.Shared`、不引用任何 NuGet 包、不引用 `Core` 下其他工程。门禁见 M1 设计 §7 第 4 条。
2. **零业务词**：类型名 / 字段名 / 方法名不得命中 ADR 0017 §六 的 AC-02 词表（`PurchaseOrder` / `Supplier` / `AmountLimit` / `TaxCode` / `SAP` / `用友` / `金蝶` / `Modbus` 等）。注释与说明文字不算违规。
3. **不透明 JSON**：`ActionRequest.Arguments` 是 `JsonElement`，底座只透传与留痕，不解析字段、不据此决策。这是「底座问，但不理解」的落点。

## 命名绑定

F5/X 组 11 条契约存在性验收靠 `src/Core/Ports/` + `src/Domains/Erp/` 的**源码标记扫描 + 反射类型名**判定，名字同时是判据与扫描词。改名会让预期红不转绿；确需改名必须同步改测试并写进 ADR。清单见 M1 设计 §3.4。

本块交付后应当转绿的只有「契约存在」类用例：**F5.1 / F5.2 / F5.3 / F5.5 / F5.6 / F5.7**。
F5.4（挂起恢复）、F5.8（假 ERP）、X1（换域假动作集）、X2（无模型仍拒绝）、X3（审计反查）**仍应红** —— 它们属于 §9 第 3/4/6/7 步，红是正确信号，不要靠往契约里塞标记词让它变绿。

## 待裁决（不自行决定）

- **`ActionStatuses` 是字符串常量**：M1 设计 §3.1 写「复用 `AgentRunStatus`/`AgentRunCode`」，但 §7 门禁 4 要求 Ports 零 ProjectReference，两者冲突。本块按门禁 4 取字符串，由网关/组合根对齐取值。转正前需定：是把终态码搬进 Ports，还是让 Ports 保持字符串 + 加一条取值一致性测试。
- **`ErrorEnvelope` 只有名字**：字段构成等 ADR 0017 §三 补齐，不臆造。
- **一次 run 是否允许多动作 / 幂等粒度**：M1 设计 §10 待确认 3，属设计决定，不在这块。

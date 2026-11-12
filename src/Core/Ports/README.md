# Core.Ports — 底座与动作域之间唯一的契约边界

- **性质**：只有接口与 record，**零实现、零业务词、零 NuGet 包、零 ProjectReference**。`Domains/*` 唯一允许 import 的工程。

## 三条不能破的约束

1. **零依赖**：不引用 `AgentWorkbench.Shared`、不引用任何 NuGet 包、不引用 `Core` 下其他工程。门禁见 `DomainSwapAc01Tests`。
2. **零业务词**：类型名 / 字段名 / 方法名不得命中 ADR 0017 §六 的 AC-02 词表（`PurchaseOrder` / `Supplier` / `AmountLimit` / `TaxCode` / `SAP` / `用友` / `金蝶` / `Modbus` 等）。注释与说明文字不算违规。
3. **不透明 JSON**：`ActionRequest.Arguments` 是 `JsonElement`，底座只透传与留痕，不解析字段、不据此决策。这是「底座问，但不理解」的落点。

## 命名绑定

F5/X 组 11 条契约存在性验收靠 `src/Core/Ports/` + `src/Domains/Erp/` 的**源码标记扫描 + 反射类型名**判定，名字同时是判据与扫描词。改名会让预期红不转绿；确需改名必须同步改测试并写进 ADR。

## 待裁决（不自行决定）

- **`ActionStatuses` 是字符串常量**：曾考虑复用 `AgentRunStatus`/`AgentRunCode`，但 Ports 零 ProjectReference，两者冲突。本块取字符串，由网关/组合根对齐取值。转正前需定：是把终态码搬进 Ports，还是让 Ports 保持字符串 + 加一条取值一致性测试。
- **`ErrorEnvelope` 只有名字**：字段构成等 Py 仓 ADR 0017 §三 补齐，不臆造。
- **一次 run 是否允许多动作 / 幂等粒度**：属设计决定，不在这块。

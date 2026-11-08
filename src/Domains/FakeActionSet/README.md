# Domains/FakeActionSet — 第二套假动作集（AC-01 换域载体）

- **用途**：M1 设计 §5.3 的 AC-01「换域后底座 diff 为空」演练载体。组合根把第一动作域的 `IActionSet` 换成 `FakeActionSet`，`src/Core` 一行不改，即可证明底座业务无关。
- **字段与 Erp 全不同**：使用 `lab_id / slot_hours / attendees`、时长与人数档位、`LB-2026-XXXX` 预约单号、审批人「实验室管理员·李」；与第一动作域（Erp）的业务字段、策略、判据、题库无任何交集。
- **依赖**：只 `ProjectReference` 到 `Core.Ports`（`..\..\Core\Ports\AgentWorkbench.Ports.csproj`），零 NuGet 包，不引用 ActionGateway、其它 Core 工程或 Erp 工程。
- **接口**：实现与 Erp 同一组 `Core.Ports` 接口（`IActionSet` / `IActionSpecRegistry` / `ISchemaValidator` / `IPermissionQuery` / `IBusinessRule` / `IApprovalGate` / `IActionExecutor`），命名空间 `AgentWorkbench.LabBooking`，主类 `FakeActionSet`。

| 文件 | 职责 |
|---|---|
| `LabActionSpecs.cs` | 4 个 `ActionSpec` 实例（`lab.query/draft/submit/cancel`）+ 注册表 |
| `LabPolicies.cs` | 假 Schema 校验、假 PDP、假业务规则（时长 / 人数判据 + 条款快照）、审批闸 |
| `InMemoryFakeLab.cs` | 四原语内存实现；按 `request_id` 去重；超时只查证不重放 |
| `FakeActionSet.cs` | `IActionSet` 组合，组合根唯一替换点 |

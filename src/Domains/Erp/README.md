# Domains/Erp — 第一动作域（M1 假实现）

- **依据**：`docs/设计/M1-受控动作执行流程设计.md` §5。全部业务语义集中在本工程。
- **依赖**：只允许引用 `Core.Ports`；`Core` 下任何项目不得引用本工程（AC-01 门禁）。

| 文件 | 职责 |
|---|---|
| `ErpActionSpecs.cs` | 4 个 `ActionSpec` 实例（`erp.query/draft/submit/cancel`）+ 注册表 |
| `ErpPolicies.cs` | 假 Schema 校验、假 PDP、假业务判据（金额档位 + 条款快照）、审批闸 |
| `InMemoryFakeErp.cs` | 四原语内存实现；按 `request_id` 去重；超时只查证不重放 |
| `ErpActionSet.cs` | `IActionSet` 组合，组合根唯一注入点 |

**验收问题对得上**：「PO-2026-1108 这张 4.8 万的备件单能批吗」→ 金额 48000 ≤ 50000 档位，`ErpBusinessRule` 放行并给出 `clause-3` 快照；`SideEffecting=true` 触发挂起审批；`Submit` 生成唯一单号。

**刻意没做**：真 ERPNext、真实权限四维、真实制度文档检索 —— 都是 M2/M3 范围。

# 工作流模块（Workflow）

这个模块只干一件事：**把「Agent 结论 → 报告」编排成可续跑、可重跑、提示词可版本化的长任务**。

它不写图引擎 —— 节点、连边、进程内执行、执行进度快照都用 MAF `Workflows`；本模块只保留
**框架不给的部分**：步骤策略、幂等登记、业务台账、单步重跑、提示词版本、进度通知。

---

## 一、现在已经具备的能力

### 运行生命周期

- 建运行：`CreateRunAsync(documentId, workflowKey, promptVersion)` —— 文档必须已解析成功、提示词版本必须存在，
  否则拒绝创建（不是跑到一半才炸）。
- 查：`GetRunAsync` / `ListRunsAsync`；删：`DeleteRunAsync`；停：`CancelAsync`（按 runId 持有 CTS）。
- 执行：后台单读者队列 `WorkflowRunQueue` + `WorkflowWorker` 调 `ExecuteAsync`。

### 检查点与重跑（2026-11-07 换实现）

- **执行进度的唯一权威是 MAF `CheckpointManager` 快照**：`ExecuteAsync` 用
  `RunStreamingAsync` / `ResumeStreamingAsync`，`sessionId = runId.ToString()`，
  持久化用框架给的 `FileSystemJsonCheckpointStore`（落 `DataDir/checkpoints`，可由
  `Workflow:CheckpointDirectory` 覆盖）。框架在每个 superstep 结束时自动 commit。
- `workflow_steps` **降级为业务台账**：只供 REQ-18 查询、REQ-19 断连可查、前端进度与审计，
  **不参与放行判定**。节点里不再有 `if (succeeded) return message;`。
- `ResumeAsync(runId)`：把 run 置回 `pending` 重新入队；`ExecuteAsync` 按
  `GetLatestCheckpointAsync(sessionId)` 判断新跑还是从断点续。Worker 与队列零改动。
- **每次执行都重建一张同拓扑的图**：MAF 的 `Workflow` 实例被 runner 独占，恢复时不能复用；
  因此 `definition.Key` 作为 executor id 必须稳定，改工作流定义会让旧 checkpoint 失效。
- `RerunStepAsync(runId, stepKey)` **返回新 runId**：单步重跑 = 新建 run（旧 run 保留为审计历史）。
  框架没有删除 checkpoint 的 API，新 runId 同时给出新 sessionId 与新幂等键。
  `POST /runs/{id}/steps/{stepKey}/rerun` 的 202 体里 `runId` 是新 run，`previousRunId` 是原 run。

### 幂等登记：把框架的「至少一次」收敛成「最多一次」

框架恢复会重新投递 pending events，只保证至少一次。`workflow_idempotency` 表（M028）是业务事实层：

| 状态 | 含义 | 节点行为 |
| --- | --- | --- |
| `in_flight` | 本次动作已登记、尚未完成 | 正常执行，成功后写 `completed` |
| `completed` | 动作成功过 | 重放直接复用 `output` / `external_ref`，不再调外部系统 |
| `unknown` | 开始过却没正常完成（超时 / 崩溃 / 进程被杀） | **拒绝自动重放**，抛「需查证」；查证后 `ResolveAsync` 才能继续 |

- 键是 `runId:stepKey`；`INSERT OR IGNORE` 保证并发重放只有一次插入生效。
- **超时不重试、只查证**（ADR 0016 B2 / F9 REQ-SAFE-03）：`WorkflowStepExecutorNode` 的
  `WorkflowStepTimeoutException` 会 `MarkUnknownAsync` 并落 `failed`，绝不自动重放。
- `WorkflowStepRecord` 新增 `IdempotencyKey` / `ApprovalId` / `ExternalRef` 三列；
  `WorkflowStepContext.IdempotencyKey` 供 F8 动作步骤透传给被调系统。

### 步骤策略

- 一个步骤类型一个类：`IWorkflowStepExecutor` + `WorkflowStepRegistry`（按 `type` 解析，未知类型直接报错）。
- 现有两种步骤：`agent`（先自行检索依据并拼进输入，再托管 `IAgentHarness` 生成结论——harness 不再二次检索）、
  `report`（把前序步骤产物汇总成 Markdown）。
- 步骤级重试与超时：`maxAttempts` + `timeoutSeconds`（linked CTS）；重试耗尽 → `WorkflowStepFailedException`，
  步骤落 `failed`。**超时是特例，不重试**（见上）。
- 步骤间传产物：`WorkflowStepContext.FindOutput(stepKey)`。
- 进度通知：`IWorkflowProgressNotifier`（默认空实现，Webapi 用 `WorkflowHub` 覆盖）。
- 提示词：`IPromptStore` 解析 / 列表 / 新增 / 激活，运行可指定 `promptVersion`。

### 接口

| 接口 | 成员 | 实现 |
| --- | --- | --- |
| `IWorkflowService` | `GetCapabilitiesAsync`、`ListDefinitionsAsync`、`CreateRunAsync`、`GetRunAsync`、`ListRunsAsync`、`CancelAsync`、`RerunStepAsync`、`ResumeAsync`、`DeleteRunAsync`、`ExecuteAsync` | `WorkflowService` |
| `IWorkflowStepExecutor` | `Type`、`ExecuteAsync(context)` | `AgentStepExecutor`、`ReportStepExecutor` |
| `IWorkflowIdempotencyStore` | `AcquireAsync`、`CompleteAsync`、`MarkUnknownAsync`、`ResolveAsync` | `WorkflowIdempotencyStore`（Dapper + SQLite） |
| `IPromptStore` | `ResolveAsync`、`ListAsync`、`AddAsync`、`ActivateAsync`、`ExistsAsync` | `PromptStore` |
| `IWorkflowRunQueue` | `EnqueueAsync`、`ReadAllAsync` | `WorkflowRunQueue` |
| `IWorkflowProgressNotifier` | `NotifyAsync(...)` | 空实现（Webapi 覆盖） |

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| agent 步骤不看 `Status` / `Code` | `iteration_limit` / `loop_detected` 路径的半成品被记成 `succeeded` | 只取 `HarnessRunResult.Text` | 非 `succeeded` 即步骤失败，并带上归类码 |
| **HITL 挂起 / 人工审批** | 动作不能跨天等人批，只能整条取消 | checkpoint 已打通，`RequestInfoEvent` 挂点留了注释位，但 `IToolApprover` 零实现、前端无审批 UI | 挂起 → 人批 → `SendResponseAsync` 恢复（另立项） |
| **Harness run 层没有 checkpoint** | 只有 Workflow 能从断点续，单次 harness run 仍无 R4 恢复 | 七层矩阵第 6 层仍是「部分」 | 单独立项，不夹带进 Workflow |
| **`WorkflowService` 接线缺服务级测试** | `ExecuteAsync` 的新跑/恢复分支、事件循环没有直接红绿可依 | 契约网覆盖节点 + 框架 + store；service 依赖 DB/DI，未直测 | 补服务级集成测试或端到端 repro |
| **`Workflow` 定义 / 提示词版本变了会让旧断点失效** | 旧 run 恢复失败 | 官方要求拓扑与 executor id 完全一致；暂无版本兼容策略 | 定义版本化 + 旧断点拒绝恢复并给出明确错误 |
| `FileSystemJsonCheckpointStore` 进程独占、非线程安全 | 加并发 Worker 会抛「store already in use」 | 单读者队列刚好串行；单例注册 | 并发前先加锁或换自定义 store |
| 普通失败仍会自动重试 | 若失败发生在外部写之后，仍可能重复 | 只有超时被拦；幂等表把「未正常完成」挡在重放层 | 给带副作用步骤加显式 `HasSideEffects` 标记，禁用自动重试 |
| run 与文档强绑定 | 通用任务也得先有一份已解析文档 | `document_id` 非空，默认定义是 `document_analysis` | 抽象输入源，文档只是其中一种 |
| 单读者队列、无并发上限 | 一个慢步骤堵住整个队列 | `Channel` 单读者 | 并发上限与优先级策略 |

---

## 三、加一个步骤类型要做什么

1. 实现 `IWorkflowStepExecutor`（一个类型一个类）。
2. 注册进 `WorkflowStepRegistry`。
3. 在定义的 JSON 里用新的 `type`（`WorkflowStepDefinition`）。
4. **带外部写的步骤必须透传 `WorkflowStepContext.IdempotencyKey`**，并遵守「超时只查证」。

---

## 四、红线

1. **不重写 MAF 图引擎**：编排、路由、事件、执行进度快照交给框架，本模块只管策略、幂等登记与台账。
2. **执行进度只有一个来源**：MAF checkpoint 快照。`workflow_steps` 是它的只读投影，
   **不得重新参与放行判定**，否则会出现「框架说继续、业务说已完成」。
3. **任何重放都不得产生第二次副作用**：幂等表未 `completed` 的动作不许自动再跑，
   `unknown` 必须查证后才能 `Resolve`。
4. **放行必须按 `stepKey` 精确匹配**，不能按顺序位置猜；executor id 用 `definition.Key`，不得随版本变化。
5. **取消不是失败**：取消落 `cancelled`，失败落 `failed`，两者不能混。
6. **不用 `CheckpointManager.CreateInMemory`**：进程重启即丢，不满足 R4，也违反 ADR 0013。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

覆盖到的：

- `tests/AgentWorkbench.ApplicationTests/Workflow/WorkflowCheckpointTests.cs`（7 条）：
  幂等复用、超时不重放、未完成重放被挡、台账全 `succeeded` 仍执行（负控）、
  checkpoint 恢复跳过已完成 superstep、不传 manager 无断点（负控）、换 store 实例读旧断点。
- `tests/AgentWorkbench.DataIntegrationTests/WorkflowIdempotencyStoreTests.cs`（4 条）：
  真实 SQLite 上二次 Acquire 转 unknown、completed 不可重开、Resolve 只作用于 unknown、键独立。
- `tests/AgentWorkbench.DataIntegrationTests/WorkflowRepositoryEfTests.cs`：EF 仓储行为。

改坏对照（每条都要「退回一行 → 指定测试变红」，2026-11-07 实测 6/6 RED）：

| 改坏哪一行 | 红哪条 |
| --- | --- |
| 把 `if (succeeded) return message;` 加回节点 | `All_steps_succeeded_in_ledger_still_execute` |
| 短路 `completed` 复用分支 | `Completed_lease_reuses_output_and_skips_second_side_effect` |
| 删 `MarkUnknownAsync` 调用 | `Timeout_never_replays_and_marks_unknown` |
| 超时异常降级成普通失败 | `Timeout_never_replays_and_marks_unknown` |
| store 去掉「二次 Acquire 转 unknown」 | `Second_acquire_of_incomplete_key_is_treated_as_unknown` |
| store 去掉 `external_ref AS ExternalRef` 别名 | `Completed_key_reuses_output_and_cannot_be_reopened` |

本机 `dotnet` 不在 PATH，且 NuGet restore 报 `XPlatMachineWideSetting` path1 为 null，
复跑一律带 `--no-restore` 并写绝对路径：

```
cd D:\Code\AgentWorkbench
"C:\Program Files\dotnet\dotnet.exe" build AgentWorkbench.slnx --no-restore
"C:\Program Files\dotnet\dotnet.exe" test tests/AgentWorkbench.ApplicationTests --no-restore
"C:\Program Files\dotnet\dotnet.exe" test tests/AgentWorkbench.DataIntegrationTests --no-restore
"C:\Program Files\dotnet\dotnet.exe" test tests/AgentWorkbench.ApiContractTests --no-restore
"C:\Program Files\dotnet\dotnet.exe" test tests/AgentWorkbench.ArchitectureTests --no-restore
```

## 依赖

AgentHarness、Knowledge.Abstractions、Persistence、Domain、Shared、`Microsoft.Agents.AI.Workflows` 1.23.0。

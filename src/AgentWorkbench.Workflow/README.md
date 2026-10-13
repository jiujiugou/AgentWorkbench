# 工作流模块（Workflow）

这个模块只干一件事：**把「Agent 结论 → 报告」编排成可续跑、可重跑、提示词可版本化的长任务**。

它不写图引擎 —— 节点、连边、进程内执行都用 MAF `Workflows`；本模块只保留**步骤策略**与**持久化**：
步骤级重试 / 超时 / 取消、状态落库、检查点放行、单步重跑、提示词版本、进度通知。

---

## 一、现在已经具备的能力

### 运行生命周期

- 建运行：`CreateRunAsync(documentId, workflowKey, promptVersion)` —— 文档必须已解析成功、提示词版本必须存在，
  否则拒绝创建（不是跑到一半才炸）。
- 查：`GetRunAsync` / `ListRunsAsync`；删：`DeleteRunAsync`；停：`CancelAsync`（按 runId 持有 CTS）。
- 执行：后台单读者队列 `WorkflowRunQueue` + `WorkflowWorker` 调 `ExecuteAsync`。

### 检查点与重跑

- **检查点就是步骤状态**：`workflow_steps` 里状态为 `succeeded` 的步骤直接放行、不再执行 —— 这是续跑的根据。
- `ResumeAsync(runId)`：把 run 置回 `pending` 重新入队，已完成的步骤靠检查点跳过。
- `RerunStepAsync(runId, stepKey)`：重置该步 + 把下游标记 stale + 整图重入。

### 步骤策略

- 一个步骤类型一个类：`IWorkflowStepExecutor` + `WorkflowStepRegistry`（按 `type` 解析，未知类型直接报错）。
- 现有两种步骤：`agent`（先自行检索依据并拼进输入，再托管 `IAgentHarness` 生成结论——harness 不再二次检索）、
  `report`（把前序步骤产物汇总成 Markdown）。
- 步骤级重试与超时：`maxAttempts` + `timeoutSeconds`（linked CTS）；重试耗尽 → `WorkflowStepFailedException`，
  步骤落 `failed`。
- 步骤间传产物：`WorkflowStepContext.FindOutput(stepKey)`。
- 进度通知：`IWorkflowProgressNotifier`（默认空实现，Webapi 用 `WorkflowHub` 覆盖）。
- 提示词：`IPromptStore` 解析 / 列表 / 新增 / 激活，运行可指定 `promptVersion`。

### 接口

| 接口 | 成员 | 实现 |
| --- | --- | --- |
| `IWorkflowService` | `GetCapabilitiesAsync`、`ListDefinitionsAsync`、`CreateRunAsync`、`GetRunAsync`、`ListRunsAsync`、`CancelAsync`、`RerunStepAsync`、`ResumeAsync`、`DeleteRunAsync`、`ExecuteAsync` | `WorkflowService` |
| `IWorkflowStepExecutor` | `Type`、`ExecuteAsync(context)` | `AgentStepExecutor`、`ReportStepExecutor` |
| `IPromptStore` | `ResolveAsync`、`ListAsync`、`AddAsync`、`ActivateAsync`、`ExistsAsync` | `PromptStore` |
| `IWorkflowRunQueue` | `EnqueueAsync`、`ReadAllAsync` | `WorkflowRunQueue` |
| `IWorkflowProgressNotifier` | `NotifyAsync(...)` | 空实现（Webapi 覆盖） |

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| agent 步骤不看 `Status` / `Code` | `iteration_limit` / `loop_detected` 路径的半成品被记成 `succeeded` | 只取 `HarnessRunResult.Text` | 非 `succeeded` 即步骤失败，并带上归类码 |
| 没用 MAF 的 `CheckpointManager` | 「恢复」实际是「重跑未完成步骤」，不是从快照续；写类副作用会重复 | 自研步骤状态 | 与幂等（P2-2）一起定语义 |
| 恢复没有幂等键 | 重跑会产生第二次副作用 | 没做 | 幂等键命中时第二次调用不产生副作用 |
| run 与文档强绑定 | 通用任务也得先有一份已解析文档 | `document_id` 非空，默认定义是 `document_analysis` | 抽象输入源，文档只是其中一种 |
| 单读者队列、无并发上限 | 一个慢步骤堵住整个队列 | `Channel` 单读者 | 并发上限与优先级策略 |
| 没有暂停 / 审批 | 危险步骤没有挂起点，只能整条取消 | 未实现（P5-1） | 状态机加 `awaiting_approval`，批准后才继续 |
| 本模块几乎没有测试 | 续跑 / 重跑没有红绿可依 | 只有 `WorkflowStepRegistryTests` | 续跑正例 + 负控（取消或失败后 resume 的行为必须可辨） |

---

## 三、加一个步骤类型要做什么

1. 实现 `IWorkflowStepExecutor`（一个类型一个类）。
2. 注册进 `WorkflowStepRegistry`。
3. 在定义的 JSON 里用新的 `type`（`WorkflowStepDefinition`）。

---

## 四、红线

1. **不重写 MAF 图引擎**：编排、路由、事件交给框架，本模块只管策略与持久化。
2. **检查点只有一个来源**：步骤状态在 SQLite 里。不要再加一套内存状态，否则「谁说了算」就不清楚了。
3. **放行必须按 `stepKey` 精确匹配**，不能按顺序位置猜。
4. **取消不是失败**：取消落 `cancelled`，失败落 `failed`，两者不能混。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

目前覆盖到的（`tests/AgentWorkbench.UnitTests/Workflow/`）：步骤注册表按类型解析、未知类型报错。

应该补上的：

- 正例：前两步已 `succeeded` 的运行 `ResumeAsync` 后只执行第三步（检查点确实放行）。
- 正例：`RerunStepAsync` 后该步重跑，且下游被判 stale。
- 负控：把某步改成非 `succeeded` 再 resume，同一场景必须重跑它（证明放行判的是状态而不是顺序）。
- 负控：`maxAttempts=1` 时步骤失败必须直接落 `failed`，不进重试。

## 依赖

AgentHarness、Knowledge.Abstractions、Persistence、Domain、Shared。


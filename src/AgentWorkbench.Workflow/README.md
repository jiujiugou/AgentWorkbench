# AgentWorkbench.Workflow

P4 工作流模块（Workflow Loop）：把「抽取 → 比对 → Agent 结论 → 报告」编排成可重跑、可续跑、可版本化提示词的长任务。
编排引擎用 **MAF `Workflows`**，底座只保留步骤策略与持久化。

## 功能

| 文件 | 说明 |
| --- | --- |
| `WorkflowService.cs` | 运行生命周期：创建 / 列表 / 详情 / 取消 / 单步重跑 / 续跑 / 删除 / 执行（`InProcessExecution.RunAsync`） |
| `WorkflowModels.cs` | 步骤定义与运行/步骤视图、`WorkflowStepContext`（步骤间通过 `FindOutput(stepKey)` 取上游产物）、领域异常 |
| `Execution/WorkflowGraphBuilder.cs` | 把 DB 里的定义编译为 MAF 图：按 `order` 顺序节点连边，末节点作为输出源 |
| `Execution/WorkflowStepExecutorNode.cs` | **步骤执行器节点**：包装 `IWorkflowStepExecutor`，负责重试 / 超时 / 取消 / 状态落库 / 进度通知 / 已完成步骤放行 |
| `Execution/WorkflowStepMessage.cs` | 图内传递运行上下文（run / 文档 / chunks / 步骤记录） |
| `Steps/IWorkflowStepExecutor.cs` | 步骤扩展点 + `WorkflowStepRegistry`（按 `type` 解析，未知类型即报错） |
| `Steps/ExtractStepExecutor.cs` | `extract`：复用 P2 `ElementExtractorRegistry` → 要素 JSON |
| `Steps/CompareStepExecutor.cs` | `compare`：复用 P2 `RuleCheckerRegistry` → 问题清单 JSON |
| `Steps/AgentStepExecutor.cs` | `agent`：先检索手册/历史依据（降级不算失败），再托管 Agent Harness 生成结论（`UseKnowledge: true`） |
| `Steps/ReportStepExecutor.cs` | `report`：汇总前序步骤产物为 Markdown |
| `Prompting/IPromptStore.cs` | 提示词**版本化**存储：解析 / 列表 / 新增 / 激活（运行可指定 `promptVersion`，不存在即拒绝创建） |
| `WorkflowSeeder.cs` | 幂等种子：`contract_review` 定义（`extract→compare→agent→report`，agent 允许 2 次尝试，`timeout=120s`）+ 工业报警处置提示词 `advisor-industrial-0.1`。注：**key 沿用历史 `contract_review`，显示名与提示词已是「工业设备报警处置」**，改代码重启后按版本号原地升级 |
| `WorkflowWorker.cs` + `WorkflowRunQueue.cs` | 后台单读者队列（`IWorkflowRunQueue`） |
| `IWorkflowProgressNotifier.cs` | 步骤级进度通知扩展点（默认空实现；Webapi 用 `WorkflowHub`） |
| Webapi `WorkflowController` | HTTP 面：`api/workflow/health` · `definitions` · `runs`（建/列表/详情/删除）· `steps` · `steps/{key}/rerun` · `cancel` · `resume` · `prompts`（列表/新增/激活） |

## 接口

| 接口 | 成员 | 实现 |
| --- | --- | --- |
| `IWorkflowService` | `GetCapabilitiesAsync`、`ListDefinitionsAsync`、`CreateRunAsync`、`GetRunAsync`、`ListRunsAsync`、`CancelAsync`、`RerunStepAsync`、`ResumeAsync`、`DeleteRunAsync`、`ExecuteAsync` | `WorkflowService` |
| `IWorkflowStepExecutor` | `Type`、`ExecuteAsync(context)` | `Extract` / `Compare` / `Agent` / `ReportStepExecutor` |
| `IPromptStore` | `ResolveAsync`、`ListAsync`、`AddAsync`、`ActivateAsync`、`ExistsAsync` | `PromptStore` |
| `IWorkflowRunQueue` | `EnqueueAsync`、`ReadAllAsync` | `WorkflowRunQueue` |
| `IWorkflowProgressNotifier` | `NotifyAsync(runId, stepKey, status, attempts, output)` | `NullWorkflowProgressNotifier`（Webapi 覆盖） |
| `IAgentHarness`（AgentHarness） | `RunAsync` | `AgentStepExecutor` 消费 |
| `IWorkflowRepository`（Persistence） | 定义 / run / step / prompt 读写 + `ResetStepAsync` / `MarkDownstreamStaleAsync` | — |

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `WorkflowBuilder`（节点 + `AddEdge` + `WithOutputFrom` + `Build`） | **MAF**（`Microsoft.Agents.AI.Workflows`） | `WorkflowGraphBuilder.Build` |
| `Executor<TIn,TOut>` / `IWorkflowContext`（执行器基类与上下文） | **MAF** | `WorkflowStepExecutorNode` |
| `InProcessExecution.RunAsync`（进程内图执行、路由、事件分发） | **MAF** | `WorkflowService.ExecuteAsync` |
| 步骤级**重试 / 超时**（`maxAttempts` + `timeoutSeconds` + linked CTS） | 自研（策略） | `WorkflowStepExecutorNode.ExecuteWithRetryAsync` |
| 步骤级**检查点**（DB 落状态；已 `succeeded` 的步骤直接放行） | 自研 | `WorkflowStepExecutorNode` + `WorkflowStepStatuses` |
| **单步重跑**（重置该步 + 下游标记 stale 后整图重入） | 自研 | `RerunStepAsync` + `MarkDownstreamStaleAsync` |
| **断点续跑**、取消（`ConcurrentDictionary<runId, CTS>`）、run 终态判定 | 自研 | `WorkflowService` |
| 步骤扩展点注册表 / 未知类型报错 | 自研 | `WorkflowStepRegistry` |
| 提示词版本化 + 种子（工业报警场景、手册未覆盖即拒答） | 自研 | `IPromptStore` / `WorkflowSeeder` |
| 步骤产物传递（`WorkflowStepMessage` + `Output` 字段 + `FindOutput`） | 自研 | 消息载荷与取值约定 |
| 后台执行 / 进度通知 | 自研 | `Channel` + `BackgroundService` + `IWorkflowProgressNotifier` |

> ⚠️ **未使用 MAF Checkpoint**：`InProcessExecution.RunAsync` 调用的是**不带 `CheckpointManager`** 的重载，
> 检查点完全由 SQLite 侧步骤记录实现。
>
> 📌 文档漂移：`docs/Agent底座-能力矩阵与建设路径.md` §二③ 仍写「仓库未使用 MAF Workflow 引擎」，与当前代码不符（现已采用）。

## 依赖

Review、AgentHarness、Knowledge.Abstractions、Persistence、Domain、Shared。

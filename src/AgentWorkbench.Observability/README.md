# AgentWorkbench.Observability

可观测与评估模块（功能域 6）：**执行轨迹的落库与回放** —— 回答「Agent 到底做了什么」。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ExecutionTrace.cs` | 契约模型：`ExecutionTrace`（SessionId / Status / Iterations / Model / LatencyMs / Error / Steps）+ `ExecutionStep`（Iteration / Kind / Tool / Arguments / Result / Error，其中 Error 仅工具失败步非空）；`Success` / `Failure` 两个工厂 |
| `IExecutionTraceStore.cs` | 契约：`SaveAsync(trace)` → runId、`GetAsync(runId)` → 轨迹或 `null` |
| `ExecutionTraceStore.cs` | 实现：写 Persistence 的 `IAgentRunRepository`（M014 `agent_runs` / M015 `agent_steps`） |
| `ObservabilityServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroObservability` |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IExecutionTraceStore` | `SaveAsync`、`GetAsync` | `ExecutionTraceStore` | AgentHarness `RunAsync`（成功与失败都落库）/ `GetRunAsync` |

## 与其他模块的分工

| 关注点 | 归属 |
| --- | --- |
| 轨迹写入/回放（持久化） | **本模块** |
| 轨迹怎么采（迭代号/工具名/参数/结果） | `ToolExecution.ToolExecutionPipeline` |
| 轨迹查询 HTTP 面 | Webapi `HarnessController`（`GET /api/harness/runs/{id}`） |
| token/成本/成功率统计 | `ModelRuntime.ModelUsageMeter`（用量）+ `GET /api/harness/health`（能力位）；成本与成功率报表未落地 |

## 依赖

Persistence、Domain、Shared。

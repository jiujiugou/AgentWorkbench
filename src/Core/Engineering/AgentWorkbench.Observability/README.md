# 可观测与评估模块（Observability）

这个模块只干一件事：**把「这次 run 到底干了什么」如实落库，并能按 runId 原样读回来**。

它只存不算：终态和归类码怎么判定是 `AgentHarness` 的事，每一步怎么采集是 `ToolExecution` 的事，
指标口径还没落地（当前只有模型用量计数在 `ModelRuntime`）。

---

## 一、现在已经具备的能力

### 一条轨迹长什么样

- `ExecutionTrace`：`SessionId` / `Status` / `ErrorCode` / `Iterations` / `Model` / `LatencyMs` /
  `Error` / `Steps`；`ExecutionStep`：`Iteration` / `Kind` / `Tool` / `Arguments` / `Result` / `Error`。
  `Error` 只有工具失败步才非空，**「失败」和「返回空」分得开**。
- 两个字段各回答一个问题：`Status` 说「以什么状态结束的」（`succeeded` / `iteration_limit` /
  `loop_detected` / `budget_exceeded` / `timeout` / `cancelled` / `failed`），
  `ErrorCode` 说「为什么」（`model_unavailable` / `tool_error` / `run_timeout` / `model_timeout` /
  `loop_detected` / `budget_exceeded` / `iteration_limit` / `cancelled` / `unhandled_error`）。
  词表在 `Shared/Execution`，判定在 `AgentHarness`。

### 怎么写进去

- `SaveAsync(trace)` → 返回 `runId`，写 `agent_runs`（M014，`error_code` 由 M022 加）与 `agent_steps`
  （M015，`error` 由 M021 加）。
- **成功、失败、取消、超时四条路径都落库**，由 `AgentHarness` 调用；失败 / 取消路径用
  `CancellationToken.None` 写，不因为调用方已取消就丢记录。

### 怎么读出来

- `GetAsync(runId)` → 轨迹或 `null`，HTTP 面是 `GET /api/harness/runs/{id}`。
- 落库前会截断：助手文本 400 字符（`AgentHarness.MaxAssistantChars`），工具参数 / 结果按
  `Harness:ToolExecution:MaxResultChars`（默认 400）。

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IExecutionTraceStore` | `SaveAsync`、`GetAsync` | `ExecutionTraceStore` | `AgentHarness.RunAsync` / `GetRunAsync` |

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 只能按 runId 单查 | 「最近有哪些失败」只能翻库 | 只有 `GetAsync(runId)`，无列表 | 按状态 / 错误码 / 时间 / sessionId 查（缺口清单 P3-3） |
| 一次 run 的落库不是原子的 | run 写进去了、steps 没写进去，回放缺一半 | `InsertRunAsync` 与 `InsertStepsAsync` 各开一条连接、无事务 | 同一事务写入 |
| 没有对话关联 | 从一次聊天跳不到它的 run | `agent_runs` 没有 `conversation_id` | 加关联列，并定回填口径 |
| 轨迹没有保留期 | 表只涨不落 | 无归档、无清理 | 按天数 / 条数归档或清理 |
| 成功率、工具失败率报表未落地 | 只有原始数据、没有结论 | 用量在 `ModelRuntime`，本模块无指标 | 定指标口径（成功 run / 总 run、P95、工具失败率） |
| 没有落库往返测试 | 字段映射改坏没人发现 | 测试里只有 Fake / Recording 替身 | 临时 SQLite 上「写 → 读」往返 + 负控（不存在的 id 返回 `null`） |

---

## 三、给轨迹加一个字段要改几处

五处一起改，少一处就会出现「写进去了但读不出来」：`Domain` 的记录 → 新迁移 `M0xx` → `ExecutionTrace`
→ 仓储 SQL → `GetAsync` 的映射。加列一律 `.Nullable()`，老数据没有值。

---

## 四、红线

1. **只存不算**。终态与归类码的判定在 `AgentHarness`；本模块改了判定，等于两处逻辑打架。
2. **失败 / 取消 / 超时也必须留痕**，且写库失败不能掩盖原始异常（调用方用 `PersistQuietlyAsync` 兜住）。
3. **超长必须截断**。参数与结果都是外部输入，不能让它把轨迹表撑爆。
4. **不改已发布的迁移**，加表加列走新号。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

目前覆盖到的：轨迹在 Harness 测试里被替身记录（`RecordingTraceStore` / `FakeTraceStore`），
验证的是「失败也调用了 `SaveAsync`、状态与归类码传对了」—— **不是**本模块的落库行为。

应该补上的（需要 SQLite 测试夹具）：

- 正例：`SaveAsync` 之后 `GetAsync` 能拿回同样的状态、归类码与逐步痕迹。
- 负控：`GetAsync` 一个不存在的 runId 必须返回 `null`。
- 负控：按错误码查询时，不存在的错误码必须返回空集（等 P3-3 的查询面做出来）。

## 依赖

Persistence、Domain、Shared。


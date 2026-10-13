# Agent 运行时编排门面（AgentHarness）

这个模块只干一件事：**把模型交互 / 工具调用 / 执行控制 / 上下文 / 记忆 / 可观测装配成一条流水线，
一次 run 走一遍，并把这次 run 的结局归类**。

它**自身不实现任何一项能力**：迭代循环本体由 MAF 承担，超时与预算在 `AgentLoop`，工具在 `ToolExecution`，
上下文在 `Context`，会话在 `Memory`，痕迹在 `Observability`。

---

## 一、现在已经具备的能力

### 一次 run 的流水线

```
ResolveAsync(端点)                                   Providers
  → IModelRuntime.Wrap(client)                       模型交互（超时 / 重试 / 并发 / 用量）
  → IAgentLoop.CreateGuard(用量来源)                  执行控制（一次 run 一个守卫）
  → IToolExecutionPipeline.Create(client, 预算+守卫)   工具调用（工具环 + 轨迹采集 + 环内终止）
  → ChatClientAgent（工具来自 IToolCatalog）           对话运行时 + 白名单目录（Safety）
  → 用户原文直接进模型（知识检索走 search_knowledge 工具，harness 不暗中拼装）
  → ISessionStore.GetOrCreateAsync(sessionId)         记忆
  → IAgentLoop.ExecuteAsync(...)                      执行控制（run 级超时）
  → 收尾分类（循环 / 预算 / 跑满 / 取消 / 超时 / 失败）  执行控制 + 编排层
  → IExecutionTraceStore.SaveAsync(轨迹+状态+归类码)    可观测（四条路径都落库）
```

### 会话怎么被用起来

- `SessionEnabled=true` 时按 `sessionId` 从 `ISessionStore` 取会话；关掉就每次新建。
- **每轮只把本轮输入装进消息**（`new ChatMessage(ChatRole.User, userText)`），上文记忆依赖
  `AgentSession` 里的 `InMemoryChatHistoryProvider`。会话本身由 `ISessionStore` 决定存哪：
  内存版程序重启即丢，装 `Memory.Persistence`（`AddNitroMemoryPersistence`）后落 `agent_sessions` 表，重启可续。
- run 结束（含失败 / 取消 / 超时）会 best-effort `SaveAsync`，落库失败不影响本次 run 的结局。
- run 开始时把 `IMemoryScopeAccessor` 设为 `MemoryScope.Session(sessionId)`：模型调 `remember` / `recall`
  时才会落在「这个会话」名下。让编排层设作用域（而不是让工具参数指定）是隐私边界，见 ADR-0003。

### 结局怎么定

一次 run 不只回 `Text`，还回**终态**（`Status`）和**归类码**（`Code`），两者都在这里判定并落库：

| 终态 `Status` | 归类码 `Code` | 什么时候落这个 | 落库路径 |
| --- | --- | --- | --- |
| `succeeded` | `null` | 模型自己给出文本收尾，没触发任何预算 | 正常返回 |
| `iteration_limit` | `iteration_limit` | 跑满 `MaxIterations`，框架清空工具逼模型收口 | 正常返回 |
| `loop_detected` | `loop_detected` | 守卫在环内判出「同工具 + 同参数」连续重复 | 正常返回（环内 `Terminate`） |
| `budget_exceeded` | `budget_exceeded` | 守卫在环内判出 token 超预算 | 正常返回（环内 `Terminate`） |
| `timeout` | `run_timeout` | 整轮超出 `TimeoutSeconds` | 异常路径 |
| `timeout` | `model_timeout` | 单次模型请求超时（`ModelRuntime` 抛 `TimeoutException`） | 异常路径 |
| `cancelled` | `cancelled` | 调用方取消 | 异常路径 |
| `failed` | `model_unavailable` | 未配置模型 / 解析不到端点 | 起跑前 |
| `failed` | `tool_error` | 工具异常冒泡出环（连续失败越限） | 异常路径 |
| `failed` | `unhandled_error` | 其余未归类异常 | 异常路径 |

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IAgentHarness` | `GetCapabilitiesAsync`、`RunAsync`、`GetRunAsync` | `AgentHarnessService` | Webapi `HarnessController`、Workflow `AgentStepExecutor` |

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| run 不能恢复 | 中途失败只能整条重跑，长任务白烧 token | 没有 resume 入口，也没有断点快照 | 先定断点语义（配合幂等 P2-2）再实现 |
| 会话重启即丢 | 同一个 `sessionId` 重启后接不上上文 | `Memory.Persistence` 已落库（P1-3）；内存版仍进程内 | 装 `AddNitroMemoryPersistence`（已具备） |
| 同一 `sessionId` 的并发 run | 两个 run 共用一个会话，消息可能串 | 无锁、无排队 | 明确「同会话串行」或按 run 隔离会话 |
| 历史无限长 | 上下文窗口撑爆 | 没有裁剪（`Context` 未实现） | token 预算压缩 |
| 执行记录不能按条件查 | 只能一条一条按 id 翻 | `IExecutionTraceStore` 只有 `Save` / `Get` | P3-3 查询面 |
| 对话与轨迹不关联 | 从一次聊天跳不到它的 run | `agent_runs` 没有 `conversation_id` | 加关联列 |
| 消费方只读 `Text` | 半成品被当结论（`iteration_limit` 等路径） | Workflow 的 agent 步骤如此 | 按 `Status` 分流，非 `succeeded` 即步骤失败 |

---

## 三、加一项新能力要做什么

**不在本模块写实现。** 正确做法是：在对应模块里写能力 → 在这里装配进流水线 → 更新
`HarnessCapabilities`（前端和探活靠它知道「这一项到底有没有」）。

---

## 四、红线

1. **不实现能力，只装配**。这里出现 `if (工具名 == ...)` 之类的业务判断就是走错了层。
2. **失败 / 取消 / 超时也必须落库**，一律 `CancellationToken.None` 写（`PersistQuietlyAsync`），
   且写库出错不能掩盖原始异常。
3. **catch 顺序是契约**：`AgentRunTimeoutException` 必须排在 `TimeoutException` 之前，否则
   「整轮太久」会被吞成「单次请求太久」，归类码就错了。
4. **取消不是失败**，靠 `cancellationToken.IsCancellationRequested` 区分「用户停的」和「跑太久了」。
5. **`Status != succeeded` 不算成功交付**。`Text` 可能是半成品。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

目前覆盖到的（`tests/AgentWorkbench.UnitTests/Harness/`）：

- 环内提前终止：连续同工具同参数 → `loop_detected`；**负控**：关掉检测 → 跑满 `MaxIterations`。
- 工具连续失败到上限 → `failed` + `tool_error`，每一步带失败原因；**负控**：只改一个故障旋钮，结果跟着变。
- 失败 / 取消 / 超时路径都调用了 `SaveAsync`，且状态与归类码正确。
- run 级超时与单次请求超时被归类成不同的码。

还没有的：真·跨进程重启会话延续（验收脚本未落地；`Memory` 侧已有进程内等价用例）、恢复后不重复副作用（P2-2）、按状态分流的消费口径。

## 依赖

ModelRuntime、ToolExecution、AgentLoop、Context、Memory、Observability、Providers、
Knowledge.Abstractions、Shared。


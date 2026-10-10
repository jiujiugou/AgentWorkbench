# Agent Harness（运行时横切轴）· 设计步骤

> 目的：为 P0–P6 的竖切能力提供统一的**运行时底座**——loop（循环/预算/终止/轨迹）、context（上下文编排/压缩/引用）、memory（会话/长期记忆）、tools（工具注册/审批）、trace（可观测/审计）。
> 定位：**横切轴（H）**，不替代任何 P；P3 的 Agent Loop、P4 的 Workflow Loop 都跑在这条轴上。
> 原则：**直接采用后端框架 MAF（Microsoft Agent Framework）已有原语作为底座**，仓库只做"契约 + 薄适配 + 组合 + 策略"，不重复造轮子。
> 底座版本（已按包内 XML 文档核对）：`Microsoft.Agents.AI 1.23.0` / `Microsoft.Agents.AI.Abstractions 1.23.0` + `Microsoft.Extensions.AI 10.10.x`（含 `Microsoft.Extensions.AI.Evaluation`）+ `Microsoft.Extensions.VectorData.Abstractions 10.10.0` + `Microsoft.ML.Tokenizers 2.0.0`。
> 依据：`../测试标准/Agent-测试与验收标准.md`（判定型 + 度量型；**能脚本/程序判定的禁止用 AI**）。
>
> 实现状态：**H0/H1 已落地**（`src/AgentWorkbench.AgentHarness` + `scripts/acceptance-harness.ps1`，11 断言全绿；P3 回归 24/24）。H2 Context 压缩、H3 持久化会话记忆、H4 完整工具集与 OpenTelemetry 待办。

---

## 0. 背景：为什么需要这条轴

P0–P6 是**竖切（按交付能力）**：解析 → 知识库 → 抽取比对 → 审查 Agent → 工作流 → 权限审计 → 评测。但 loop / context / memory / tools / trace 属于**运行时横切子系统**：每个阶段都在用，却没有一个阶段拥有它。

现状缺口（代码事实）：

| 关注点 | 现状 | 问题 |
| --- | --- | --- |
| loop | `ReviewAdvisor/Agent/ReviewAgentLoop.cs`（手写、私有） | 只有审查 Agent 有；通用对话 Agent 走框架默认，无统一预算/终止/轨迹契约 |
| context | `RagService.BuildSystemPrompt` / `ChatService.BuildMessages` | 散落、无预算/压缩/引用绑定抽象 |
| memory | 无 `conversations/messages` 表；历史由前端传入 | 无会话记忆，更无长期记忆 |
| tools | `IAgentToolProvider` 有接口、**实现为 0** | 对话 Agent 实际"裸聊" |
| trace | 仅 P3 局部落库 | 无统一可观测/审计 |

而 **MAF 1.23 已内置这些原语**（见 §3）。因此本设计的核心不是"设计算法"，而是**选择 MAF 原语 + 定义薄契约 + 组合 + 落策略**。

---

## 1. 目标与范围

| 做 | 不做 |
| --- | --- |
| 以 MAF 原语为底座，统一 loop/context/memory/tools/trace 的**契约与组合** | 重写 MAF 已提供的循环/会话/压缩/审批 |
| 为 P3 审查 Agent 提供**迁移路径**（行为不变，P3 验收保持绿） | 改变 P3/P2 的对外契约与验收口径 |
| 为 P4 工作流提供 loop/trace 托管 | 通用可视化拖拽编排器（仍不做） |
| memory 最小闭环（会话记忆持久化 + 隔离 + 级联） | 长期记忆的高级策略（摘要/遗忘曲线）先做"可插拔接口"，默认关闭 |
| trace/用量统一（为 P5 审计、P6 评测提供数据面） | 用 LLM-as-judge 做门禁（测试铁律禁止） |

---

## 2. 横切轴总览（设计图）

```
                         ┌─────────────────────── Harness 运行时（横切轴 H）───────────────────────┐
                         │                                                                            │
                         │   Loop 运行时        Context 编排         Memory            Tools/审批        │
                         │  (预算/终止/轨迹)   (注入/压缩/引用)   (会话/长期)      (白名单/授权)     │
                         │        ▲                  ▲                ▲                ▲            │
                         │        │                  │                │                │            │
                         │        └──────────────────┴────────────────┴────────────────┘            │
                         │                        Trace / 用量 / 可观测（贯穿）                      │
                         └───────────────────────────────────────┬────────────────────────────────────┘
                                                                 │ 全部基于
                         ┌───────────────────────────────────────▼────────────────────────────────────┐
                         │        底座：MAF (Microsoft.Agents.AI 1.23) + Microsoft.Extensions.AI         │
                         │  AIAgent / ChatClientAgent / LoopAgent / AgentSession / AIContextProvider     │
                         │  ChatHistoryProvider / Compaction / TextSearchProvider / ToolApprovalAgent    │
                         │  LoggingAgent / OpenTelemetryAgent / UsageAggregator                          │
                         └───────────────────────────────────────┬────────────────────────────────────┘
                                                                 │ 复用仓库既有
                         ┌───────────────────────────────────────▼────────────────────────────────────┐
                         │  Provider 网关（IChatClientFactory → IChatClient，已有多供应商/多模型）        │
                         │  Knowledge（IKnowledgeEngine/HybridRetriever，FTS5 + sqlite-vec）             │
                         │  Persistence（SQLite + FluentMigrator + Dapper）                              │
                         └──────────────────────────────────────────────────────────────────────────────┘

 横切使用关系（谁用谁）：
   P3 审查 Agent   → Loop + Tools + Trace（现 ReviewAgentLoop 收敛为底座的一种 loop 配置）
   P4 Workflow     → Loop + Context + Trace（把 agent_step 当一种步骤托管）
   P5 权限审计     → Trace + Tools 审批策略
   P6 评测兜底     → Trace + 回放 + 用量
   通用对话(H/F)   → Loop + Context + Memory + Tools + Trace
```

---

## 3. 底座：MAF 原语 → 采用策略（核心）

> 下表所有右列**均为 MAF 1.23 已提供**的类型；仓库侧只做"选择/配置/薄适配/契约"。

| Harness 关注点 | MAF 原语（1.23.0） | 仓库采用策略 |
| --- | --- | --- |
| Agent 抽象 | `AIAgent`、`ChatClientAgent`、`ChatClientAgentOptions`、`AIAgentBuilder`、`DelegatingAIAgent` | 组合根用 `AIAgentBuilder` 装配；`ChatClientAgent` 包 `IChatClient`（来自 `IChatClientFactory`） |
| 自带工具环 | `ChatClientAgentOptions`（构造注入 `IList<AITool>`）、`AITool`/`AIFunctionFactory` | 复用 `IAgentToolProvider` 产出 `AITool`，注册进 `ChatClientAgent` |
| 迭代/自循环 | `LoopAgent`、`LoopAgentOptions`（`MaxIterations`/`FreshContextPerIteration`/`NonStreamingReturnsLastResponseOnly`/`SessionCreatedCallback`）、`LoopEvaluator`、`LoopEvaluation.Stop/Continue/ContinueWithMessages`、`DelegateLoopEvaluator`、`CompletionMarkerLoopEvaluator`、`TodoCompletionLoopEvaluator`、`BackgroundTaskCompletionLoopEvaluator` | **用 `LoopAgent` + `LoopEvaluator` 取代手写 `ReviewAgentLoop`**：`MaxIterations` = maxSteps；`CompletionMarkerLoopEvaluator`/`DelegateLoopEvaluator` 判"收敛/短路"；`LoopEvaluation.Stop` 终止 |
| 上下文注入 | `AIContextProvider`、`MessageAIContextProvider`、`ChatClientAgentOptions.AIContextProviders`、`AIAgentBuilder.UseAIContextProviders(...)` | 用 provider 组装上下文，不再手拼 system prompt |
| 检索增强（RAG→context） | `TextSearchProvider` + `TextSearchProviderOptions`（`ContextPrompt`/`CitationsPrompt`/`SearchBehavior.BeforeAIInvoke` / `OnDemandFunctionCalling`/`Redactor`） | 把 `IKnowledgeEngine.Retrieve` 适配为 `TextSearchProvider` 的搜索委托；沿用强制引用 |
| 上下文压缩/预算 | `Compaction.CompactionProvider` + `CompactionStrategy`（`SlidingWindow`/`Summarization`/`Truncation`/`ToolResult`/`ContextWindow`/`Pipeline`）+ `CompactionTriggers`（`TokensExceed`/`TurnsExceed`/`MessagesExceed`/`HasToolCalls`…）+ `Microsoft.ML.Tokenizers` | 配置化启用（P4/P6）；token 预算与工具结果截断交给 MAF |
| 会话状态 | `AgentSession`、`AgentSessionStateBag`、`AgentSessionExtensions`、`AIAgent.CreateSessionAsync/SerializeSessionAsync/DeserializeSessionAsync` | 会话对象由 MAF 管理；序列化后落库 |
| 会话记忆（历史） | `ChatHistoryProvider`、`InMemoryChatHistoryProvider`（+`ChatReducer`/`ReducerTriggerEvent`） | 会话内历史默认 `InMemoryChatHistoryProvider`；**自定义 SQLite `ChatHistoryProvider`** 做持久化 |
| 会话持久化 | `AgentSessionStore`（`GetOrCreateSessionAsync`/`GetSessionAsync`/`SaveSessionAsync`）、`AgentSessionStoreKey` | **实现 SQLite `AgentSessionStore`**（会话 + 状态包落库） |
| 长期记忆 | `ChatHistoryMemoryProvider`（+`VectorStore`、`SearchScope`/`StorageScope`、`SearchBehavior`）、`FileMemoryProvider` | 向量库接既有 sqlite-vec；默认关闭，按需开启 |
| 工具审批/越权 | `ToolApprovalAgent`、`ToolApprovalAgentOptions.AutoApprovalRules`、`ToolApprovalRule` | 白名单工具 + 审批规则；越权/高风险工具走审批或直接拒 |
| 可观测 | `LoggingAgent`、`OpenTelemetryAgent`（`DefaultSourceName`/`EnableSensitiveData`） | 组合根中间件；贯穿全部 harness 调用 |
| 用量 | `UsageAggregator`、`AgentResponse.Usage`、`UsageDetails` | 汇总 token，供 P5 审计 / P6 评测 |
| 评估（可选、非门禁） | `Microsoft.Extensions.AI.Evaluation`（`IAgentEvaluator`/`LocalEvaluator`/`EvalCheck`/`AIJudgeLoopEvaluator`） | 仅离线/非门禁；**不得**进入 acceptance 门禁 |

> 关键结论：**loop/context/memory/tools/trace 在 MAF 里都有原语**，仓库的价值在于"契约、组合、策略、确定性测试"，而不是实现这些机制本身。

---

## 4. 子系统设计

### 4.1 Loop 运行时（HL）

- **基座**：`ChatClientAgent`（自带工具调用环）+ `LoopAgent`（外层迭代/预算）。
- **契约**：`IAgentLoopRunner`（仓库薄契约）
  - 入参：`AgentRunSpec`（agent 定义、初始消息、工具集、预算 `maxIterations`/`timeout`、取消 `CancellationToken`）
  - 出参：`AgentRunResult`（最终 `AgentResponse` + `Usage` + **轨迹**）
- **预算/终止**：`LoopAgentOptions.MaxIterations`（= maxSteps）、`CancellationToken`（= timeout）、`LoopEvaluator` 返回 `LoopEvaluation.Stop`（收敛/短路）。
- **轨迹**：由 `DelegatingAIAgent`/`OpenTelemetryAgent` 中间件 + `LoopContext.Iteration` 采集每步 `kind/tool/arguments/result`，落 `review_agent_steps` 同构表（P4 泛化为 `agent_steps`）。
- **迁移 P3**：`ReviewAgentLoop` 的"工具白名单 + maxSteps + 无证据短路 + 引用后校验"变为：
  - 白名单 = 注册进 `ChatClientAgent` 的 `AITool` 集合（越权工具根本不在）；
  - maxSteps = `LoopAgentOptions.MaxIterations`；
  - 无证据短路 = 调用前前置判断（不进模型）；
  - 引用后校验 = `LoopEvaluator`/结果后置程序校验。
  - **不变量**：迁移后 P3 的 AL1–AL4/N5–N7 必须保持绿（见验收 §5）。

### 4.2 Context 编排（HC）

- **组装顺序（pipeline）**：`UseAIContextProviders(...)` 依次注入
  1. `TextSearchProvider`（RAG 检索片段 + 引用，接 `IKnowledgeEngine`）；
  2. `ChatHistoryProvider` / `InMemoryChatHistoryProvider`（会话历史）；
  3. `CompactionProvider`（预算/压缩，可选）。
- **引用绑定**：沿用 `TextSearchProvider` 的 `CitationsPrompt`/`ContextPrompt`，输出仍带 `{chunkId, seq, evidence}`，与 P2/P3 证据模型同构。
- **预算**：`ContextWindowCompactionStrategy`/`SummarizationCompactionStrategy` + `CompactionTriggers.TokensExceed(...)`；token 计数用 `Microsoft.ML.Tokenizers`。
- **替换现状**：`RagService.BuildSystemPrompt` → `TextSearchProvider`；`ChatService.BuildMessages` → `ChatHistoryProvider`。

### 4.3 Memory（HM）

- **会话记忆（会话内 + 跨重启）**：
  - `AgentSession` + `AgentSessionStateBag` 保存会话状态；
  - 持久化二选一/组合：自定义 `AgentSessionStore`（存整包）或自定义 `ChatHistoryProvider`（存消息列表）；
  - 序列化用 `AIAgent.SerializeSessionAsync/DeserializeSessionAsync`。
- **长期记忆（跨会话，默认关闭）**：`ChatHistoryMemoryProvider`（`VectorStore`= sqlite-vec，`SearchScope`/`StorageScope` 按用户/会话隔离）。
- **数据模型（建议迁移，见 §6）**：`conversations`、`messages`（+ 可选 `memory_vectors` 复用既有向量表）。
- **隔离与级联**：以 `AgentSessionStoreKey`（`SessionId` + `Partitions`）做租户/用户/会话分区；删会话级联删消息/状态。

### 4.4 Tools 与策略（HT）

- **注册**：`IAgentToolProvider.GetTools()`（已有扩展点）产 `AITool`，注入 `ChatClientAgentOptions`。
- **落地 5 个内置工具**（通用平台 F3）：`get_current_time`、`calculator`（禁 eval）、`http_fetch`（协议/超时/大小限制）、`sqlite_query`（只读白名单）、`read_document`（接知识库）。
- **策略/审批**：`ToolApprovalAgent` + `ToolApprovalRule`/`AutoApprovalRules`；高风险工具走审批，白名单外直接拒绝并留痕。
- **多 Agent**：`AIAgentExtensions.AsAIFunction` 可把一个 Agent 暴露为工具（为 P4/F8 预留）。

### 4.5 Trace / 可观测 / 审计（HO）

- **中间件**：组合根按 `LoggingAgent` → `OpenTelemetryAgent` 包裹；
- **轨迹落库**：每步 `Iteration`、工具名/参数/结果、耗时、token；
- **用量**：`UsageAggregator` 汇总，写 `usage`（P5 审计、P6 评测数据面）。

---

## 5. 组合根与模块结构（建议，最终以 ADR 定）

```
Webapi
  └─ AddNitroAgentHarness(configuration)      ← 组合根：AIAgentBuilder 装配 pipeline
        ├─ Agent（已存在）                     ← AIAgent/ChatClientAgent 工厂 + IA入工具
        ├─ Harness.Abstractions（新增，契约）  ← IAgentLoopRunner / ISessionStore / IContextSource / IAgentTrace
        ├─ Harness.Local（新增，薄适配）       ← MAF 原语适配：LoopAgent/ChatHistoryProvider/TextSearchProvider/Compaction/ToolApproval
        └─ Persistence（已存在）               ← conversations/messages/agent_steps/usage 迁移 + 仓储
```

依赖方向（新增边，须 ADR 批准）：
`Webapi → Harness(.Local) → { Agent, Persistence, Providers, Knowledge.Abstractions, Domain, Shared }`；`Harness.Abstractions → Domain, Shared`；**Domain 保持纯净**。架构测试需新增切片 `Harness`。

> 备选（更轻）：不新增项目，把 harness 直接放进现有 `Agent` 模块，memory 迁移放 `Persistence`。取舍在 ADR 决定。

---

## 6. 数据模型（memory 最小闭环，建议 M014+）

| 表 | 关键列 | 说明 |
| --- | --- | --- |
| `conversations` | id, title, agent_name, created_at, updated_at | 会话 |
| `messages` | id, conversation_id(FK CASCADE), role, content, tool_call_id, usage_json, created_at | 消息（含工具消息） |
| `agent_sessions` | session_id, partition, state_json, updated_at | 可选：整包会话状态（`AgentSessionStore`） |
| `agent_steps` | run_id, iteration, kind, tool, arguments, result, created_at | P3 `review_agent_steps` 的泛化（P4 用） |
| `usage` | scope_type, scope_id, model, prompt_tokens, completion_tokens, created_at | 审计/评测 |

> 已有 `M001–M013`；本轴新增从 **M014** 起。

---

## 7. 配置（建议）

```jsonc
"Harness": {
  "Loop":   { "MaxIterations": 6, "TimeoutSeconds": 30 },
  "Context":{ "RagTopK": 5, "Compaction": { "Enabled": true, "TriggerTokens": 12000, "Strategy": "Summarization" } },
  "Memory": { "SessionPersistence": true, "LongTerm": { "Enabled": false, "Provider": "sqlite-vec" } },
  "Tools":  { "Whitelist": ["get_current_time","calculator","http_fetch","sqlite_query","read_document"] },
  "Trace":  { "OpenTelemetry": true, "SensitiveData": false }
}
```

---

## 8. 扩展点（新增 = 加类，0 改旧代码）

- `IAgentToolProvider`（已有）— 新工具
- `IAgentLoopRunner` / `ILoopEvaluator`（薄契约）— 新循环策略（复用 `DelegateLoopEvaluator`）
- `IContextSource` — 新上下文来源（复用 `TextSearchProvider`/`AIContextProvider`）
- `ISessionStore` / `ILongTermMemory` — 新存储（复用 `AgentSessionStore`/`ChatHistoryMemoryProvider`）
- `IAgentTrace` — 新 trace sink

---

## 9. 与 P3/P4/P5/P6 的顺延关系

| 阶段 | 与 H 的关系 |
| --- | --- |
| **P3** | 审查 Agent Loop 收敛为 H 的一种 `LoopAgent` 配置；**对外契约与验收不变**（AL1–AL4/N5–N7 绿） |
| **P4** | Workflow Loop 依赖 H 的 Loop + Trace；`agent_step` = H 的 loop 运行一次 |
| **P5** | 权限审计复用 H 的 Trace/审批/用量 |
| **P6** | 评测/兜底复用 H 的 Trace/回放/用量 |

---

## 10. 确定性测试策略（遵循测试铁律）

- **能脚本/程序判定的禁止用 AI**：loop 预算/终止/白名单、context 预算/压缩、memory 持久化/隔离/级联、trace 完整性 —— 全部程序判定。
- **LLM 层去 AI 化**：用**本地 mock**（沿用 P3 `src/AgentWorkbench.MockLlm` + `tests/fixtures/llm/`）驱动 `IChatClient`；MAF 原语可注入 mock `IChatClient`，无需真模型。
- **record-replay**：`AgentSession`/消息可序列化，天然支持回放。
- **不引入 LLM-as-judge 门禁**；`Microsoft.Extensions.AI.Evaluation` 仅离线参考、非门禁。

---

## 11. 分期建议（H0–H4）

| 期 | 内容 | 验收 |
| --- | --- | --- |
| **H0** | 契约 + 组合根骨架（`IAgentLoopRunner`/`ISessionStore`/`IAgentTrace`）；`AIAgentBuilder` 装配 | 架构测试新增切片；harness 健康位 |
| **H1** | Loop：`LoopAgent` 落地（预算/终止/轨迹）；**P3 迁移且保持绿** | `AgentHarness-验收标准.md` HL + P3 回归 |
| **H2** | Context：`TextSearchProvider` + `ChatHistoryProvider` + `CompactionProvider` | HC |
| **H3** | Memory：`conversations/messages` + SQLite `AgentSessionStore`/`ChatHistoryProvider` | HM |
| **H4** | Tools 5 件套 + `ToolApprovalAgent`；Trace 中间件 + 用量 | HT/HO |

---

## 12. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| MAF 版本 API 漂移（含 Experimental） | 迁移成本 | 锁定 1.23.0；契约层隔离；升级走 ADR |
| `LoopAgent` 语义与 P3 自定义协议不一致 | P3 验收回退 | 薄适配 + 保留 `LoopEvaluator` 后置校验；P3 回归门禁 |
| memory 依赖新迁移/新依赖边 | 架构测试失败 | 先 ADR 批依赖边 + 更新切片 |
| 压缩引入不确定性 | 判定困难 | 程序化预算断言（token/消息数），mock 摘要 |
| 过度抽象 | 复杂度 | 只抽 4 个薄契约；其余直接用 MAF |

---

## 13. 落地前置（实现前须办）

1. **ADR**：确立 H 为横切轴 + 模块/依赖边（符合 `docs/adr/README.md` 准入）。
2. **架构测试**：`ModuleSlices`/`AssemblyNames`/`Expected` 增 `Harness` 切片。
3. **AGENTS.md**：模块表 + 依赖方向 + 构建/验收命令。
4. **落地清单**：登记 H0–H4 与 P3/P4 顺延关系。
5. **验收脚本**：`scripts/acceptance-harness.ps1`（见配套验收文档）。

# Agent 底座 · 能力矩阵与建设路径

> **定位**：基于 Microsoft Agent Framework（MAF）的通用 Agent 运行底座。
> 不绑定任何行业业务，只解决"把 Agent 跑起来、跑得住、看得见、停得下、恢复得了"。
> **这不是产品，是运行时。**
>
> 配套文档：`docs/底座建设清单.md`（缺口清单）、`docs/验收标准/AgentHarness-验收标准.md`（可执行验收）。
> 依据：`docs/测试标准/Agent-测试与验收标准.md`（判定型 + 度量型双轨；能由程序判定的禁止用 AI）。
>
> 技术栈：.NET 10 / ASP.NET Core / `Microsoft.Agents.AI 1.23.0` / `Microsoft.Extensions.AI 10.10.x` / SQLite（Dapper + FluentMigrator） / Serilog。

---

## 一、三条铁律

违反任何一条，本文作废。

1. **不重做 MAF 已有的。** Agent Loop、工具调用协议、Workflow 引擎、Checkpoint 机制，
   MAF 已提供。底座只补它没直接满足的：**执行策略、持久化、统一接口、可靠性保证、可观测性**。
   动手前先问："MAF 能不能配置出来？"能就别写。
2. **每项能力必须配一个能变红的负控。** 只有正例绿的测试 = 假绿。
   新写的每一条都必须先写"什么情况下它必须失败"。
3. **不引入任何垂直业务语义。** 底座代码里不得出现合同 / 工业 / 仓储 / 单证等词。
   业务模块未来以独立模块接入，只依赖底座契约。

---

## 二、MAF 到底提供了什么

MAF 的定位是"机制提供者"：它把与模型交互、工具调用、工作流编排这些**通用机制**做好了，
但**策略、边界、持久化、可靠性**默认不替你做。逐项如下。

| # | 能力域 | MAF 提供的机制 | 对应类型（本仓库用法） | 成熟度 | 是否够用 |
|---|---|---|---|---|---|
| ① | **Agent 与模型交互** | `IChatClient` 抽象、`ChatClientAgent`、多轮会话、流式响应、结构化输出；多供应商解耦 | `ChatService`、`AgentHarnessService.BuildAgent`、`StructuredChatService` | 成熟 | ✅ 直接复用 |
| ② | **Tools / 工具调用** | `AIFunctionFactory` 生成工具 Schema、`FunctionInvokingChatClient` 自动执行函数环、调用分发与结果回传；可接 MCP | `IAgentToolProvider`、`BuiltinTools`、`ReviewAgentLoop.BuildTools` | 成熟 | ✅ 直接复用 |
| ③ | **Workflows / 工作流编排** | 将 Agent/函数/执行节点组织为有明确路径的流程，支持顺序、并行、条件分支、汇合、多 Agent 协作 | 仓库**自研**了 `AgentWorkbench.Workflow`，**未使用** MAF Workflow 引擎 | 成熟（但本仓库未用） | ⚠️ 已另起炉灶，保持现状 |
| ④ | **会话状态 / 记忆 / 上下文** | `ChatHistoryProvider`（含内存实现）、可扩展的上下文提供器、可接持久化与检索 | `InMemoryChatHistoryProvider`、RAG 注入（`IKnowledgeEngine`） | 提供扩展点 | ⚠️ 只给了壳，存储策略自定 |
| ⑤ | **Middleware / 中间件** | 在模型请求、响应、Agent 执行过程中插入自定义逻辑（日志/策略/过滤/异常/拦截） | `FunctionInvoker` 回调、run 级 try/catch | 扩展点 | ⚠️ 仅扩展点 |
| ⑥ | **Checkpoint / 暂停与恢复** | 对支持检查点的工作流保存执行状态并恢复；用于长任务、中断恢复、人工审批 | 仓库在 `WorkflowService` 里**手工**做了步骤级检查点 | 机制存在 | ⚠️ Harness run 无检查点 |
| ⑦ | **可观测性** | 与 OpenTelemetry 集成，追踪调用链、监控执行 | **本仓库零使用** | 机制存在 | ❌ 未接入 |
| ⑧ | **人工审批与运行控制** | 工具调用/工作流步骤前暂停等待批准；Harness 提供文件访问、上下文压缩、工具审批策略 | **本仓库零使用** | 机制存在 | ❌ 未接入 |

**一句话**：MAF 给的是"引擎 + 扩展点"，不是"策略 + 保证"。它让 Agent 能跑，但**不保证跑得住**。

---

## 三、职责边界：MAF / 底座 / 业务 各管什么

理解这张表，就理解了这个项目该写什么、不该写什么。

| 关注点 | MAF 提供 | **底座负责（本项目）** | 业务负责（未来模块） |
|---|---|---|---|
| 模型反复调用工具 | Agent 执行机制 | 最大步数、业务停止条件、终止原因 | 何时算"任务完成" |
| 工具执行失败 | 中间件 / 异常扩展点 | 错误分类、重试策略、降级规则 | 哪些错误可重试 |
| 模型陷入循环 | 可自定义执行控制 | 循环检测、调用预算、终止策略 | — |
| 对话历史过长 | 上下文提供器 | 保留什么、如何压缩、如何验证 | 领域关键上下文 |
| 任务执行中断 | Workflow Checkpoint / 恢复机制 | 持久化配置、恢复策略、**外部副作用去重** | 副作用的业务语义 |
| 危险工具调用 | 审批机制 / 扩展能力 | 权限边界、审批规则、服务端授权 | 哪些工具危险 |
| 多用户同时使用 | 可集成到 Web 服务 | 用户隔离、并发限制、任务队列、资源配额 | 用户/租户模型 |
| 任务结果正确性 | 结构化输出、工作流编排 | 只保证"输出结构正确、可追溯" | **业务规则、验证器、验收标准** |

> 关键区分：底座保证**过程正确**（跑得住、可观测、可恢复、可停止），业务保证**结果正确**（对错）。

---

## 四、现在已经完成的（禁止重写）

以下全部是仓库里的真代码，动手前先看清，别再写一遍。

| 能力 | 位置 | 证据 |
|---|---|---|
| Agent 运行时 / 工具环 | `src/AgentWorkbench.AgentHarness/AgentHarnessService.cs:163` | MAF `FunctionInvokingChatClient` + `MaximumIterationsPerRequest` |
| 对话 Agent（含流式） | `src/AgentWorkbench.Agent/ChatService.cs:106` | `ChatClientAgent.RunStreamingAsync` + 工具降级 |
| 步数上限 | `HarnessOptions.Loop.MaxIterations = 6`（`HarnessOptions.cs:19`） | 真代码 |
| 超时 / 取消 | `AgentHarnessService.cs:101` `CreateLinkedTokenSource` + `CancelAfter(30s)` | 真代码（穿透性未验） |
| 工具白名单 | `HarnessOptions.Tools.Whitelist`（`HarnessOptions.cs:43`） | 真代码 |
| 内置只读工具 | `AgentHarness/Tools/BuiltinTools.cs` | time / calculator / search_knowledge / list_documents |
| 轨迹落库 / 回放 | `M014 agent_runs` / `M015 agent_steps` + `AgentRunRepository` | `ITharnessRunRepository.GetRunAsync` |
| 会话记忆（进程内） | `AgentHarnessService.cs:35` `ConcurrentDictionary` | ⚠️ 重启即丢 |
| RAG 上下文注入 | `AgentHarnessService.cs:87` + `IKnowledgeEngine` | 真代码 |
| 结构化输出 | `IStructuredChatService` / `StructuredChatService` | 强类型 JSON |
| 模型网关 | `src/AgentWorkbench.Providers` + `IChatClientFactory` | 多供应商 / 模型解析 |
| 混合检索 RRF | `src/AgentWorkbench.Knowledge.Local` | FTS5 + sqlite-vec + RRF |
| 自研工作流（P4） | `src/AgentWorkbench.Workflow/WorkflowService.cs` | 步骤级检查点、重跑、续跑、重试、提示词版本 |
| 自研审查 Agent（P3） | `src/AgentWorkbench.ReviewAdvisor/Agent/ReviewAgentLoop.cs` | 只读工具环 + 越权终止 + 引用后置校验 |
| 任务队列 / 后台消费 | `WorkflowRunQueue`+`WorkflowWorker`、`AdvisorRunQueue`+`AdvisorWorker` | `Channel` + `BackgroundService` |
| Web 组合根 | `src/AgentWorkbench.Webapi/Program.cs` | `AddNitroXxx` 模块注册 + SignalR Hub |
| 已有验收 | `scripts/acceptance-harness.ps1`（13 断言）、`-p0`～`-p4` | 全部 mock LLM 驱动 |
| 架构边界 | `tests/AgentWorkbench.ArchitectureTests` | ArchUnitNET 强制依赖方向 |

**共同短板**：
1. 全部 mock LLM 驱动，**未在真实模型下验证过**；
2. 只有正例绿，**几乎全部缺"能变红的负控"**；
3. Harness 这一层**只有"能跑"，没有"跑得住"**（无预算、无循环检测、无错误分类、无幂等、无并发控制、无审批）。

---

## 五、还没有完成的（缺口，按优先级）

图例：`做什么` / `为什么` / **完成判据** / **负控（必须能红）** / `规模`

### P0 — 试验装置与真实验证
把 mock 绿变成真绿。不新增业务，只新增"能让底座出错"的试验工具。

- **P0-1 失败工具 `fault_injector`**（`mode=throw|timeout|hang`，`failTimes=N`）
  - 为什么：`PersistFailureAsync` 那条分支从未被真正触发；错误分类/重试/降级零覆盖。
  - 判据：连续失败达上限后**必须降级返回**，轨迹记录失败原因，`Status=failed`。
  - **负控：重试上限设为 0 后，同一用例必须 FAIL。**
- **P0-2 慢工具 `slow_op`**（`delayMs`）
  - 为什么：取消信号是否真穿透进工具内部**未验证**。
  - 判据：`delayMs > TimeoutSeconds` 时工具内部必须收到 `OperationCanceledException`。
  - **负控：`delayMs` 调到超时窗口以内，同一用例必须从"超时"变成"正常返回"。**
- **P0-3 计数工具 `counter`**（`key`）
  - 为什么：并发执行、重复调用去重零覆盖。
  - 判据：并发 10 次调用同一 `key`，最终计数**恰等于 10**。
  - **负控：去掉幂等键后重跑，计数必须 > 10。**
- **P0-4 真实模型冒烟**（单轮 / 工具调用 / 多轮）
  - 为什么：现有 13 个断言全是 mock，没跑过真模型 = 没验证过。
  - 判据：三条全部成功，轨迹完整落库。
  - **负控：抽掉 API key → 必须返回明确"未配置模型"错误，而非空结果或 500。**

> **P0 做完，底座才算"跑通过"。在此之前新增任何功能都是叠沙堡。**

### P1 — 执行控制的硬保证
- **P1-1 循环检测**：同工具同参数重复达阈值 → 终止并标记 `loop_detected`。
  - **负控：关闭检测后同一场景必须跑满 `MaxIterations`。**
- **P1-2 调用预算**：次数 / token / 时长三维，超限终止并返回已得部分，`Status=budget_exceeded`。
  - **负控：预算设为极大值，同一用例不得提前终止。**
- **P1-3 会话记忆持久化**：从进程内 `ConcurrentDictionary` 迁到 SQLite。
  - **负控：不存在的 `sessionId` → 必须新建，不得复用他人会话。**

### P2 — 可靠性
- **P2-1 错误分类**：`模型不可用 / 工具异常 / 超时 / 预算耗尽 / 取消 / 内容违规` 六类，各有错误码。
  - **负控：故意把超时归类成工具异常 → 测试必须 FAIL。**
- **P2-2 副作用去重（幂等）**：工具调用加幂等键，恢复后同一键不产生二次副作用。
  - 为什么：MAF 明确 Checkpoint **不等于**外部工具的重复执行去重。
  - **负控：去掉幂等键后恢复，计数必须变成 2。**
- **P2-3 降级策略**：`重试 → 换等价工具 → 返回占位结果 → 终止`，可配置。
  - **负控：把降级关掉 → `failTimes=99` 时整条 run 必须 failed 而非 degraded。**

### P3 — 可观测性
- **P3-1 OpenTelemetry 追踪**：每次模型调用 / 工具调用一个 span，带 `runId`、`iteration`、耗时、错误码。
  - 判据：一次含 3 次工具调用的 run，导出 ≥ 5 个 span 且父子关系正确。
  - **负控：注入一次工具异常，对应 span 必须标 Error。**
- **P3-2 指标**：run 总数 / 成功率 / P95 延迟 / 工具失败率四个计数器。
  - **负控：全部失败时成功率必须为 0（不能因未上报而显示 100%）。**
- **P3-3 失败原因可检索**：按 `Status` / 错误码 / 时间范围 / `sessionId` 查询失败 run。
  - **负控：查一个不存在的错误码必须返回空，而不是全量。**

### P4 — 并发与资源预算
- **P4-1 并发隔离**：两个 run 并发执行，轨迹、会话、计数器互不串扰。
  - **负控：故意让工具共享一个静态计数器 → 测试必须 FAIL（证明测得出串扰）。**
- **P4-2 并发上限与排队**：同时运行的 run 数上限，超出排队或 429。
  - **负控：上限设为 0 → 所有请求必须被拒，而不是全部放行。**
- **P4-3 取消的传播与清理**：取消时释放占用、落 `cancelled`、工具侧收到 token。
  - **负控：取消一个不存在的 `runId` → 必须返回明确错误而不是静默成功。**

### P5 — 审批与权限边界
- **P5-1 危险工具审批**：标 `requiresApproval` 的工具执行前暂停，等批准后继续；状态 `awaiting_approval`。
  - **负控：伪造批准信号 → 必须被拒绝。**
- **P5-2 工具级权限**：白名单从"全局配置"升级为"每次 run 可指定可用工具子集"。
  - **负控：不传子集时行为必须与现在一致（向后兼容，不能全禁）。**

### 尚未并入清单但属"良好底座"的项
- **上下文压缩 / 预算核算**（HC3/HM4/MH3）：超长历史触发压缩，注入 token ≤ 预算，压缩比 ≤ 0.60。
- **用量统计**（HO2）：`usage` 汇总 token > 0。
- **敏感数据默认不落**（HO3）：`EnableSensitiveData=false` 时日志/轨迹脱敏。
- **度量评测脚本**（`eval-harness`）：当前**不存在**，对应 `docs/验收标准/AgentHarness-验收标准.md` 的 M1–M4。

---

## 六、一个"良好"的 Agent 底座应该完成什么

分五层看。**越靠下的层越硬（可靠性），越靠上的层越软（策略）。**

```
┌─────────────────────────────────────────────┐
│ 5. 治理层   权限边界 · 审批 · 配额 · 多租户隔离      │
├─────────────────────────────────────────────┤
│ 4. 可观测层  Trace(span) · Metrics · 失败检索        │
├─────────────────────────────────────────────┤
│ 3. 可靠性层  错误分类 · 重试 · 降级 · 幂等去重        │
├─────────────────────────────────────────────┤
│ 2. 控制层    循环检测 · 预算 · 终止 · 压缩 · 取消传播   │
├─────────────────────────────────────────────┤
│ 1. 执行层    Agent Loop · 工具环 · 会话 · 上下文      │  ← MAF 负责
└─────────────────────────────────────────────┘
         ▲ Web 服务：任务队列 · 并发网关 · REST/Hub
```

### 质量属性（底座的"好"如何定义）

| 属性 | 含义 | 判定方式 |
|---|---|---|
| **可运行** | 一条命令独立启动，无需业务模块也能跑通闭环 | `acceptance-harness` 绿 |
| **可终止** | 任何异常输入都不会悬挂；总有明确终态 | 死循环样本终止率 = 100%（MH1） |
| **可靠** | 工具失败可重试/降级；恢复不重复副作用 | B5/B9 正例绿 + 负控红 |
| **可恢复** | 中断后能从中断点续跑，状态持久化 | 重启后同 session 可续、run 可 resume |
| **可观测** | 每次模型/工具调用可追踪、可诊断、可度量 | B12/B13；span 父子正确 |
| **可隔离** | 多任务互不串扰，资源有上限 | B7/B8 |
| **可治理** | 危险调用需授权，权限可按 run 收窄 | B10/B11 |
| **可扩展** | 新工具/新策略 = 加实现，0 改旧代码 | 架构测试 + 扩展点 |
| **可测试** | 全部关键行为由脚本判定，不依赖真模型 | mock + 负控双轨 |

### 扩展点契约（新增 = 加类 / 加接口实现）

- `IAgentToolProvider` — 注入 MAF 函数工具
- `IKnowledgeEngine` + `KnowledgeEngineRegistry` — 知识引擎
- `IEmbeddingProvider` / `IReranker` — 向量与重排
- `IChatClientFactory` — 模型网关
- `IStructuredChatService` — 强类型结构化输出
- `IWorkflowStepExecutor` / `IPromptStore` — 工作流步骤与提示词
- （待补）`IApprovalGate` / `IHarnessQueue` / `IHarnessConcurrencyGate` — 治理与资源

---

## 七、总验收表（判定型 + 度量型双轨）

判定型——每项绿/红，且**正例绿时负控必须红**：

| 编号 | 能力 | 正例 | 负控 | 阶段 |
|---|---|---|---|---|
| B1 | 正常执行 | 模型调工具并返回 | 工具抛异常 → 必须 failed | P0 |
| B2 | 循环终止 | 重复调用在第 N 次终止 | 关检测 → 跑满 MaxIterations | P1 |
| B3 | 超时 | SlowTool 超时被取消 | 延迟调小 → 正常返回 | P0 |
| B4 | 预算 | 超预算终止 | 预算极大 → 不提前终止 | P1 |
| B5 | 工具失败降级 | `failTimes=99` 时降级 | 关降级 → 整条 failed | P2 |
| B6 | 取消 | 取消信号进到工具内部 | 取消不存在的 run → 报错 | P4 |
| B7 | 并发隔离 | 10 并发轨迹不串 | 共享计数器 → 必须 FAIL | P4 |
| B8 | 并发上限 | 上限 2 时最多 2 个在跑 | 上限 0 → 全拒 | P4 |
| B9 | 幂等 | 恢复后副作用只一次 | 去幂等键 → 两次 | P2 |
| B10 | 审批 | 未批准不执行 | 伪造批准 → 拒绝 | P5 |
| B11 | 权限 | 子集外工具被拒 | 不传子集 → 行为不变 | P5 |
| B12 | 追踪 | 3 次工具调用 ≥5 span | 注入异常 → span 标 Error | P3 |
| B13 | 失败检索 | 按错误码精确筛出 | 不存在 → 返回空 | P3 |

度量型——给出数字与趋势，由 `eval-harness` 程序计算：

| 指标 | 口径 | 阈值 |
|---|---|---|
| MH1 终止率 | 不收敛样本终止率 | = 100%（悬挂 = 0） |
| MH2 引用可核对率 | 带引用结论可核对比例 | = 100% |
| MH3 压缩比 | 压缩后 token / 原始 token | ≤ 0.60 |
| MH4 会话记忆命中率 | 依赖记忆金标问答命中率 | ≥ 0.80 |
| — run 成功率 | 成功 run / 总 run | 趋势 |
| — P95 延迟 | 全部 run 的 95 分位耗时 | 趋势 |
| — 工具失败率 | 失败工具调用 / 总工具调用 | 趋势 |
| — 平均迭代数 | 反映循环检测是否误伤 | 趋势 |
| — 降级率 | degraded 结束的 run 占比 | 趋势 |

---

## 八、建设路径与顺序

**严格按顺序，做完一级再进下一级。**

| 阶段 | 内容 | 时间盒 | 完成判据 |
|---|---|---|---|
| 第 1 步 | P0 全部（试验装置 + 真实模型冒烟） | 1 天 | 负控能红 |
| 第 2 步 | P1 执行控制 | 1 天 | B2/B3/B4 绿且负控红 |
| 第 3 步 | P2 可靠性 | 2 天 | B5/B9 绿且负控红 |
| 第 4 步 | P3 可观测性 | 1 天 | B12/B13 绿且负控红 |
| 第 5 步 | P4 并发 | 1 天 | B6/B7/B8 绿且负控红 |
| 第 6 步 | P5 审批权限 | 1 天 | B10/B11 绿且负控红 |
| **合计** | | **7 天** | 13 条判定全绿 |

**止损线**：第 1 步做完后，如果负控红不了 10 条以上，说明**验收方法本身有问题**——
停下来修验收，不要继续写功能。

**每阶段验证命令**：

```powershell
dotnet build AgentWorkbench.slnx
dotnet test tests/AgentWorkbench.ArchitectureTests
dotnet test tests/AgentWorkbench.UnitTests
powershell -ExecutionPolicy Bypass -File scripts/acceptance-harness.ps1
# 阶段 P3 起：
python scripts/eval-harness.py      # -> tests/benchmark/harness/report.json
```

全过退出码 0。

---

## 九、明确不做（避免范围膨胀）

- ❌ 不做任何垂直业务模块（维持终裁：底座零业务语义）
- ❌ 不做模型训练 / 微调 / 模型管理平台
- ❌ 不做可视化工作流设计器（拖节点 UI）
- ❌ 不做多租户 SaaS / 计费 / 组织管理
- ❌ 不做前端管理后台（现有 web 只演示，不加功能）
- ❌ **不重写 MAF 已有的 Agent Loop / 工具协议 / Workflow 引擎**
- ❌ 不把知识库（RAG）继续做深——它已够用，评测交给 `tests/benchmark/`

---

## 十、开工前自检（每加一个能力就问）

> **MAF 能不能配置出来？** 能 → 别写。
> **这条测试有没有能变红的负控？** 没有 → 别写，先写负控。
> **它跟业务有关吗？** 有关 → 别写在底座里。

---

## 附录 A：环境前置

| 项 | 现状 | 动作 |
|---|---|---|
| PowerShell | 本机仅 5.1，无 pwsh | 新脚本一律用 Python（推荐） |
| API key | `appsettings.json` 的 `Provider.ApiKey` 为空 | 需要一个可用 key，否则 P0-4 无法进行 |
| 后端进程 | 易被回收，锁 `bin/**/*.dll` | 验证前确认端口存活 |
| 数据库 | 生效的是 `src/AgentWorkbench.Webapi/data/agentworkbench.db` | 根目录 `data/` 是旧的，别看错 |
| 向量模型 | 配的 ollama/bge-m3，默认不启动 | 需要时先 `ollama serve` |

## 附录 B：关键文件索引

| 关注 | 文件 |
|---|---|
| 底座主服务 | `src/AgentWorkbench.AgentHarness/AgentHarnessService.cs` |
| 底座配置 | `src/AgentWorkbench.AgentHarness/HarnessOptions.cs` |
| 底座模型 | `src/AgentWorkbench.AgentHarness/HarnessModels.cs` |
| 内置工具 | `src/AgentWorkbench.AgentHarness/Tools/BuiltinTools.cs` |
| 运行轨迹表 | `src/AgentWorkbench.Persistence/Migrations/M014_CreateAgentRuns.cs`、`M015_CreateAgentSteps.cs` |
| 轨迹仓储 | `src/AgentWorkbench.Persistence/AgentRunRepository.cs` |
| HTTP 入口 | `src/AgentWorkbench.Webapi/Controllers/HarnessController.cs` |
| 组合根 | `src/AgentWorkbench.Webapi/Program.cs` |
| 已有验收 | `scripts/acceptance-harness.ps1` |
| 可执行验收标准 | `docs/验收标准/AgentHarness-验收标准.md` |

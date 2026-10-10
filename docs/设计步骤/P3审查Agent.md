# P3 审查 Agent（Agent Loop）· 设计步骤

> 目的：对 P2 产出的**风险/不一致清单（findings）与要素（elements）**，通过一个**受限的 Agent Loop**自动检索证据、做上下文核验与影响分析，生成**带原文引用的审查结论 + 补正建议**，把"清单"变成"可核对的结论"。
> 定位：**P3 = Agent Loop（内层：模型驱动、受限、逐条 finding）**；**P4 = Workflow Loop（外层：步骤定义 / 单步重跑 / 提示词版本 / 重试）**。P3 不自建通用编排。
> 范围：**不出法律意见、不判输赢**；沿用 P0 chunks、P1 检索、P2 证据模型。
> 依据：`法律文件Agent-MVP与实施指南.md` ④（工作流末步「汇总风险报告」）+ ⑦（Agent）；`docs/测试标准/Agent-测试与验收标准.md`（判定型 + 度量型；能脚本判定的禁止用 AI）。
> 不含：通用工作流编排（P4）、权限与审计（P5）、评测平台化（P6）；本阶段仅内置**最小兜底**（无证据不下结论）与**最小指标**（覆盖率/引用可核对率）。

## 0. 与既有模块/文档的适配

| 事项 | 采用 |
| --- | --- |
| Loop 分层 | **P3=Agent Loop（内层）**；**P4=Workflow Loop（外层）**；两者接口 = "外层给预算，内层回结果 + 轨迹" |
| Agent 底座 | 复用 Microsoft Agent Framework（MAF）+ `IAgentToolProvider`（仓库已有） |
| 证据/引用 | 复用 P2 `chunkId/seq/pageFrom/pageTo/evidence` 与 P1 `?chunk=seq` 高亮 |
| 检索 | 复用 P1 `IKnowledgeEngine.Retrieve`（FTS5 + 向量 + RRF） |
| 结构化输出 | 复用 `IStructuredChatService.GetJsonAsync<T>`（JSON + 一次修复重试） |
| 提示词版本化 | **P4 再做**；P3 用代码内常量 prompt（记录 `prompt_version` 字符串） |
| 权限/审计 | **P5 再做**；P3 仅埋**最小审计点**（记录每次模型调用，先落日志） |
| 评测/兜底 | **P6 平台化**；P3 仅内置最小兜底与最小指标 |

## 1. 目标与范围

| 做 | 不做 |
| --- | --- |
| 逐条 P2 finding：受限循环内检索/核验证据 → **带引用结论 + 补正建议** | 出具法律意见、判断案件输赢 |
| **无证据不下结论**（短路为 `insufficient`，建议人工复核） | 编造事实/伪造引用 |
| 结论可点回原文高亮（复用 P2 证据定位） | 训练/微调模型 |
| 结构化输出 + 最小量化指标 + **轨迹留痕** | 通用工作流编排（P4）/ 权限审计（P5） |

> 边界约束：`suggestion` 只允许"**文档层面的补正/核对建议**"；`impact` 只允许"**条款层面影响说明**"，不得出现胜负/责任归属判断。

## 2. 双层 Loop 与职责边界

| | **Agent Loop（P3，内层）** | **Workflow Loop（P4，外层）** |
| --- | --- | --- |
| 谁决定控制流 | **模型**（调哪个工具、调几次） | **人**（预定义步骤与顺序） |
| 状态机 | 自循环，直到收敛/短路/超预算 | 固定步骤图 + 步骤级状态 |
| 归属 | 逐条 finding 的"取证与下结论" | 步骤定义、**重试/取消/超时/进度**、检查点、**提示词版本**、单步重跑 |

**接口（钉死）**：外层向 Agent Loop 传入**预算**（工具白名单 + 最大步数 + 超时）与上下文（finding + chunks + 检索入口），内层返回**结果（结论/`insufficient`）+ 轨迹**。P4 把 P3 当作一种步骤类型（`agent_step`）托管。

> P3 **不负责**：步骤定义、单步重跑、提示词版本化、跨步编排——这些是 P4。语义上 P3 的循环体按"步骤形状"写（明确输入/输出/预算/轨迹），以便 P4 收编。

## 3. 组件选型（复用既有）

| 用途 | 组件 | 来源 |
| --- | --- | --- |
| Agent Loop 运行时 | MAF + `IAgentToolProvider` | Agent（已有） |
| 结构化结论输出 | `IStructuredChatService.GetJsonAsync<T>` | Agent |
| 检索证据 | `IKnowledgeEngine` | Knowledge.Abstractions |
| 取 findings/elements | `IReviewService` / `IReviewRepository` | Review（P2） |
| 取 chunks | `IDocumentRepository.GetChunksAsync` | Persistence |
| 后台执行 | `Channel<long>` + `BackgroundService` | 复用 P2 模式 |
| 进度推送 | SignalR（复用 `/hubs/review`） | Webapi |

## 4. 数据模型

### 4.1 `review_conclusions`（审查结论）— 迁移 **M011**

| 列 | 类型 | 说明 |
| --- | --- | --- |
| id | INTEGER PK identity | |
| run_id | INTEGER NOT NULL | FK `review_runs(id)` **ON DELETE CASCADE** |
| finding_id | INTEGER NULL | FK `review_findings(id)` **ON DELETE CASCADE** |
| status | TEXT | `concluded` / `insufficient` |
| title | TEXT | 一句话结论标题 |
| conclusion | TEXT NULL | 结论正文（`insufficient` 时为空或"资料不足"） |
| suggestion | TEXT NULL | 补正/核对建议（受 §1 边界） |
| impact | TEXT NULL | 条款层面影响说明（受 §1 边界） |
| basis | TEXT | `evidence` / `no-evidence` |
| confidence | REAL | 0–1 |
| source | TEXT | `llm` / `rule` |
| chunk_id | INTEGER NULL | FK `chunks(id)` **ON DELETE SET NULL** |
| seq / page_from / page_to | INTEGER NULL | 引用定位（跳 `?chunk=seq`） |
| evidence | TEXT NULL | 引用原文片段 |
| citations | TEXT NULL | JSON 数组：`[{chunkId,seq,evidence}]` |
| prompt_version | TEXT NULL | 使用的 prompt 版本（P4 前为常量） |
| steps_used | INTEGER NULL | 本 finding 消耗的 Agent Loop 步数 |
| created_at | TEXT | ISO-8601 |

索引：`ix_review_conclusions_run(run_id)`。

### 4.2 `review_agent_steps`（Agent Loop 轨迹）— 迁移 **M012**

| 列 | 类型 | 说明 |
| --- | --- | --- |
| id | INTEGER PK identity | |
| run_id | INTEGER NOT NULL | FK `review_runs(id)` **ON DELETE CASCADE** |
| finding_id | INTEGER NULL | FK `review_findings(id)` **ON DELETE CASCADE** |
| step_index | INTEGER NOT NULL | 该 finding 内的步序（从 0） |
| kind | TEXT | `tool` / `assistant` / `final` |
| tool | TEXT NULL | 工具名（白名单内） |
| arguments | TEXT NULL | 调用参数（JSON） |
| result | TEXT NULL | 结果摘要（JSON/文本） |
| created_at | TEXT | ISO-8601 |

索引：`ix_review_agent_steps_run(run_id, finding_id)`。

## 5. 模块结构与 Agent Loop

```
ReviewAdvisor/                              ← 新增模块（P3）
  IReviewAdvisorService                    生成/查询结论与轨迹
  ReviewAdvisorService                     编排：取 findings → 逐条跑 Agent Loop
  AdvisorRunQueue / AdvisorWorker          Channel<long> + BackgroundService
  Agent/ReviewAgentLoop                    受限循环：工具白名单 + 预算 + 终止
  Agent/Tools/                             retrieve_evidence / read_chunk / locate_evidence
  Evidence/EvidenceLocator                 程序判定：引用是否存在、证据是否命中 chunk
  Prompting/AdvisorPrompts                 结论 prompt（常量 + version）
  Models/ConclusionDto                     结构化输出契约
  ReviewAdvisorServiceCollectionExtensions  AddNitroReviewAdvisor
Controllers/AdvisorController              /api/review/runs/{id}/advisor|conclusions|advisor/trace
```

依赖方向（单向）：`Webapi → ReviewAdvisor → { Review, Knowledge.Abstractions, Agent, Persistence, Domain, Shared }`。

> 架构测试需新增切片 `ReviewAdvisor`：`slnx`、`TestArchitecture.AssemblyNames`、`ModuleCycleTests.expected`、`LayeringTests`、`Webapi.csproj`。

**Agent Loop（逐条 finding，受限）**

```
预算：工具白名单 {retrieve_evidence, read_chunk, locate_evidence}；maxSteps=6；timeout=30s

for finding in run.findings:
  ① 程序前置检索（不进模型）：retrieve_evidence(由 finding 关键词构造 query)
       - 结果为空 → status=insufficient, basis=no-evidence，**不调用模型**（短路）
  ② Agent Loop（模型驱动，受预算约束）：
       loop step in 0..maxSteps-1:
         模型输出：要么"工具调用"，要么"最终结论 JSON"
           · 工具调用 → 执行白名单工具 → 记录轨迹 → 观察 → 继续
           · 最终结论 → break
       超 maxSteps / 超 timeout → 终止（按已有证据判定或 insufficient）
  ③ 程序后校验：结论引用必须通过 EvidenceLocator（chunk 存在且 evidence 命中）
       不通过 → 降级 insufficient
  ④ 落库 conclusion + 轨迹(review_agent_steps) + steps_used
```

**五条硬约束（不可省）**：
1. **工具白名单**：只读三工具；越权工具直接拒绝并记轨迹。
2. **最大步数 + 超时**：防不收敛/死循环。
3. **轨迹留痕**：每步 `kind/tool/arguments/result`。
4. **无证据短路在模型之前**：零证据不进模型。
5. **引用后校验**：程序判定引用真实性，不通过即降级。

- **降级原则**：未配置模型时 `advisor=false`，不产结论、不挂起。
- **最小兜底**：无证据/引用不可核对 → 一律 `insufficient`，不编造。

### 4.3 `advisor_jobs`（结论生成任务）— 迁移 **M013**

| 列 | 类型 | 说明 |
| --- | --- | --- |
| id | INTEGER PK identity | |
| run_id | INTEGER NOT NULL | FK `review_runs(id)` **ON DELETE CASCADE**（唯一） |
| status | TEXT | `pending` / `running` / `succeeded` / `failed` |
| error | TEXT NULL | 失败原因 |
| created_at / finished_at | TEXT | ISO-8601 |

索引：`ix_advisor_jobs_run(run_id)` 唯一。

## 6. API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/reviewadvisor/health` | 能力位 `{ advisor, llm }` |
| POST | `/api/review/runs/{id}/advisor` | 逐条生成结论（202 `{ runId }`，入队） |
| GET | `/api/review/runs/{id}/advisor` | 任务状态（`pending/running/succeeded/failed` + `error` + 结论数） |
| GET | `/api/review/runs/{id}/conclusions` | 结论列表（`status/basis` + 引用定位 + `stepsUsed`） |
| GET | `/api/review/runs/{id}/advisor/trace` | Agent Loop 轨迹（供判定型断言） |
| GET | `/api/review/runs/{id}/advisor/report?format=md\|json` | 结论汇总报告（可选） |

- 重跑：`POST advisor` 按 run 重建（先删后插，幂等）。
- 进度：复用 `/hubs/review` 推 `ReceiveAdvisorStep/Done/Error`。

## 7. 前端页面

- `ReviewDetailView.vue`：清单每行「查看结论」→ 结论卡片（结论/建议/影响 + 引用「片段#n」→ `?chunk=seq`）；`insufficient` 显示"资料不足，建议人工复核"。可选展示轨迹（调试）。
- `api/review.ts`：新增 `advisor(runId)`、`conclusions(runId)`、`advisorTrace(runId)`。

## 8. 任务拆解（建议 4–6 天）

| 天 | 任务 | 验收 |
| --- | --- | --- |
| D1 | 新模块骨架；M011/M012；`AddNitroReviewAdvisor`；架构切片；`/health` | build 通过；架构 3/3；health 返回 `advisor` |
| D2 | `EvidenceLocator` + 前置检索短路 + 工具三件套 | 无证据 100% `insufficient`，模型未被调用 |
| D3 | `ReviewAgentLoop`（预算/终止/轨迹）+ `ConclusionDto` + 后校验 | 有证据产出带可核对引用；轨迹完整；不超 maxSteps |
| D4 | API（advisor/conclusions/trace）+ 队列 worker + 进度 | 创建→运行→查询闭环；重跑幂等 |
| D5–D6 | 前端结论面板 + `scripts/acceptance-p3.ps1` + `scripts/eval-p3.ps1` | 端到端；覆盖率/引用可核对率达标 |

## 9. 扩展点（新增 = 加类）

- `IReviewAdvisorService`（新结论策略）
- `IAgentTool`（新只读工具，注册进白名单）
- `IConclusionRenderer`（新导出格式，后置）

## 10. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| 结论幻觉 | 不可信 | 强制引用 + 程序后校验 + 无证据短路 |
| Loop 不收敛/失控 | 挂起/成本失控 | maxSteps + timeout + 工具白名单 + 轨迹 |
| 越界（法律意见/输赢） | 合规/口碑 | `suggestion/impact` 限文档层面；prompt 禁止；验收含越界负控 |
| 逐条调用成本 | 体验差 | 后台队列 + 每 run findings 上限 + 超时取消 |
| 结构化输出失败 | 脏数据 | JSON + 一次修复重试；失败该条 `insufficient` |

## 11. 与测试标准的衔接

- **判定型（用 mock 模型/工具，确定性）**：覆盖率、引用可核对、无证据短路、**不超 maxSteps、必终止、工具白名单、轨迹完整**、结构化合法、级联、未配模型不挂起。
- **度量型（程序算 + 人工校准）**：引用可核对率、覆盖率；**建议合理率**人工抽样（不引入 LLM 判）。
- 交付 `scripts/acceptance-p3.ps1`（判定 + 负控）与 `scripts/eval-p3.ps1`（指标 + 阈值，小规模金标）。

## 12. 实现状态（2026-10-05）

- ✅ 新模块 `AgentWorkbench.ReviewAdvisor`（EvidenceLocator / Agent Loop / 工具白名单 / 服务 / 队列 / worker / DI）。
- ✅ 迁移 M011 `review_conclusions`、M012 `review_agent_steps`、M013 `advisor_jobs` + `AdvisorRepository`。
- ✅ `AdvisorController`（health / advisor / status / conclusions / trace）+ Program 注册 + 架构测试切片。
- ✅ 单元测试：`ReviewAgentLoopTests`（final-concluded / 无依据降级 / 工具步 / 超 maxSteps 终止 / 越权工具拒绝 / 模型空输出）、`EvidenceLocatorTests`。
- ✅ `scripts/acceptance-p3.ps1`（本地 mock 驱动，A1–A5/AL1–4/N1–N7/L1 走 HTTP 层自动断言；含无模型第 2 实例）。
- ✅ `scripts/eval-p3.ps1`（M1/M2/M3 指标 + 金标 → `tests/benchmark/p3/report.json`）。
- ✅ 本地 mock：`src/AgentWorkbench.MockLlm` + `tests/fixtures/llm/`（normal/badref/loop/illegal/nonjson/extract）。
- ✅ P3 金标与负控样本：`tests/benchmark/p3/golden/`、`no-evidence/`。
- ⏳ 待办：前端结论面板（P3 判定不依赖）。

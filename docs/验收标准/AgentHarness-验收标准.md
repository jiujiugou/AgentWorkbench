# Agent Harness（运行时横切轴）· 验收标准（可执行版）

> 对应设计：`../设计步骤/AgentHarness.md`
> 底座：Microsoft Agent Framework `Microsoft.Agents.AI 1.23.0` + `Microsoft.Extensions.AI 10.10.x`
> 判定原则：每条给出**可复核证据**（脚本结果 / 接口返回）。**全部自动化**，不含人工抽样、不含 LLM-as-judge。
> 依据：`../测试标准/Agent-测试与验收标准.md`（**能由脚本/程序判定的，绝对不用 AI**；LLM 层用 **mock 模型 + record-replay** 确定性化）。

## 0. 判定方式说明

| 标记 | 含义 |
| --- | --- |
| **自动** | 由 `scripts/acceptance-harness.ps1` 断言，给出 PASS/FAIL |
| **度量** | 由 `scripts/eval-harness.ps1` 程序计算指标 + 阈值门禁 |

> 本轴**不含人工项**：主观"建议是否合理"属 P3 Q1，不在此表。loop/context/memory/tools/trace 的判定全部程序化（mock 驱动）。

## 1. 功能验收（判定型）

| 编号 | 标准 | 判定 | 阈值 / 断言 | 证据 |
| --- | --- | --- | --- | --- |
| HL1 | Loop 有界 | **自动（mock）** | 单次 run 的迭代数 ≤ `MaxIterations`（轨迹 `iteration` 最大值 ≤ 配置） | 脚本输出 |
| HL2 | 必定终止 | **自动（mock）** | mock 诱导不收敛 → 到 `MaxIterations`/`timeout` 终止；run 终态非悬挂 | 脚本输出 |
| HL3 | 工具白名单 | **自动（mock）** | 仅白名单工具可执行；非白名单工具被拒且**留痕**（轨迹含拒绝记录） | 脚本输出 |
| HL4 | 轨迹完整 | **自动** | 每步含 `iteration/kind/tool/result`；结果 `stepsUsed` 与轨迹步数一致 | 脚本输出 |
| HL5 | P3 迁移不变量 | **自动** | 迁移到 `LoopAgent` 后 `scripts/acceptance-p3.ps1` 仍 **24/24 PASS**（行为不变） | 脚本输出 |
| HC1 | 检索注入 | **自动（mock）** | RAG 命中片段作为 context 注入；注入消息带来源标记 | 脚本输出 |
| HC2 | 引用可核对 | **自动** | 结论引用 `{chunkId,seq,evidence}`：`seq` ∈ 文档真实 chunks 且 `evidence` 命中该 chunk | 脚本输出 |
| HC3 | 预算 / 压缩 | **自动（mock）** | 超预算样本触发 `Compaction`；注入 token/消息数 ≤ 预算；未超预算时最近 N 轮不丢 | 脚本输出 |
| HC4 | 无命中拒答 | **自动** | 检索无命中 → 明确"资料不足"，不产生无引用结论 | 脚本输出 |
| HM1 | 会话持久化 | **自动** | 同一 `AgentSessionStoreKey` 跨进程重启后可回放历史（消息条数/内容一致） | 脚本输出 |
| HM2 | 会话隔离 | **自动** | 不同 session/partition 的消息互不可见（无串话） | 脚本输出 |
| HM3 | 删除级联 | **自动** | 删会话后 `conversations`/`messages`/`session state` 全消失 | 脚本输出 |
| HM4 | 长会话稳定 | **自动（mock）** | 超长会话由 history reducer/compaction 处理，不报错、不超预算 | 脚本输出 |
| HT1 | 工具注册调用 | **自动（mock）** | 白名单工具可被模型调用并返回结果，事件可见 | 脚本输出 |
| HT2 | 工具审批策略 | **自动（mock）** | 需审批工具在未批准时不执行且留痕；命中 `AutoApprovalRule` 才放行 | 脚本输出 |
| HT3 | 越权拒绝 | **自动（mock）** | 白名单外工具请求 → 拒绝、不执行、留痕 | 脚本输出 |
| HO1 | 轨迹落库 | **自动** | 每步写 `agent_steps`；字段完整 | 脚本输出 |
| HO2 | 用量统计 | **自动** | `usage` 汇总 token > 0（mock 也应有可选计数字段） | 脚本输出 |
| HO3 | 敏感数据默认不落 | **自动** | `EnableSensitiveData=false` 时日志/轨迹不含完整敏感原文（脱敏） | 脚本输出 |

## 2. 度量验收

| 编号 | 标准 | 判定 | 阈值 | 证据 |
| --- | --- | --- | --- | --- |
| MH1 | 终止率 | **度量** | 不收敛样本终止率 **= 100%**（悬挂 = 0） | `eval-harness` 报告 |
| MH2 | 引用可核对率 | **度量** | 带引用结论的可核对比例 **= 100%** | `eval-harness` 报告 |
| MH3 | 压缩比 | **度量** | 超预算样本压缩后 token / 原始 token **≤ 0.60** | `eval-harness` 报告 |
| MH4 | 会话记忆命中率 | **度量** | 金标问答中依赖会话/长期记忆的命中率 **≥ 0.80**（默认关闭时标 X 豁免） | `eval-harness` 报告 |

> 指标均为**程序计算**（对照 `tests/benchmark/harness/golden/`），不调用模型做判断。

## 3. 负控（红队）——必须存在，且必须"会失败"

| 编号 | 负控输入 | 判定 | 期望结果 |
| --- | --- | --- | --- |
| NH1 | mock 诱导死循环 | **自动（mock）** | 到 `MaxIterations`/`timeout` 终止（HL2 复验），不挂起 |
| NH2 | mock 请求白名单外工具 | **自动（mock）** | 拒绝、不执行、留痕（HT3 复验） |
| NH3 | mock 返回非 JSON / 结构化失败 | **自动（mock）** | 该步降级、不写脏数据、run 有明确终态 |
| NH4 | 未配置模型 | **自动** | 能力位 `harness/llm=false`；请求明确 4xx 或 run `failed` 且 `error` 非空，不挂起 |
| NH5 | 超长上下文（超预算） | **自动（mock）** | 触发压缩/截断，不崩溃、不超预算 |
| NH6 | 不存在的 session | **自动** | 拒绝或新建空会话，**不得**串到他人会话 |
| NH7 | 压缩摘要器失败 | **自动（mock）** | 回退到截断/滑窗策略，run 仍终止 |

## 4. 测试集

- 复用：`tests/corpus/`、`tests/benchmark/p2/negative/`、`tests/benchmark/p3/`、`tests/fixtures/llm/`。
- 新增 `tests/benchmark/harness/`：
  - `golden/`：会话脚本 + 期望（注入来源、压缩后预算、记忆命中）。
  - `negative/`：死循环 / 越权工具 / 非 JSON / 超长上下文 / 缺 session。
  - `long-conversation.json`：超长多轮样本，供 HC3/HM4/MH3。
- 本地 mock：沿用 `src/AgentWorkbench.MockLlm`（如需新增动作在 `tests/fixtures/llm/` 增场景），**不得调用真模型**。

## 5. 自动化脚本

```powershell
# 判定 + 负控（构建 → 清库 → 起 mock+服务 → 跑 → 停）
powershell -ExecutionPolicy Bypass -File scripts/acceptance-harness.ps1
# 指标（终止率 / 引用可核对率 / 压缩比 / 记忆命中率）
powershell -ExecutionPolicy Bypass -File scripts/eval-harness.ps1
```

- 断言：HL1–HL5 / HC1–HC4 / HM1–HM4 / HT1–HT3 / HO1–HO3 + NH1–NH7。
- 全部用 **mock** 确定性判定，不依赖真模型。
- 退出码：全过 `0`；有 FAIL `1`；环境/前置失败 `2`。
- 输出：每个断言 PASS/FAIL 与明细；`eval-harness` 输出 `tests/benchmark/harness/report.json`。

## 6. 放行判定

- **自动**：HL/HC/HM/HT/HO 全 PASS + NH1–NH7 全 PASS。
- **度量**：MH1/MH2/MH3 达标（MH4 未启用长期记忆时标 X 豁免）。
- **回归**：`scripts/acceptance-p3.ps1` 仍 24/24（HL5）。
- 同时满足 → **Agent Harness 通过**。

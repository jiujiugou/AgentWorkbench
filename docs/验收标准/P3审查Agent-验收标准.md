# P3 审查 Agent · 验收标准（可执行版）

> 对应设计：`../设计步骤/P3审查Agent.md`（P3 = Agent Loop，P4 = Workflow Loop）
> 判定原则：每条给出**可复核证据**（脚本结果 / 接口返回 / 截图），不靠感觉。
> 分工：**A 类自动断言（脚本，确定性，不做 LLM 判断）｜度量型（程序算指标 + 人工校准）｜人工抽样**。
> 依据：`../测试标准/Agent-测试与验收标准.md`（**能由脚本/程序判定的，绝对不用 AI**；Agent Loop 用 **mock 模型 + mock 工具**确定性化）。

## 0. 判定方式说明

| 标记 | 含义 |
| --- | --- |
| **自动** | 由 `scripts/acceptance-p3.ps1` 断言，给出 PASS/FAIL/SKIP |
| **度量** | 由 `scripts/eval-p3.ps1` 程序计算指标 + 阈值门禁；主观项人工校准 |
| **人工** | 抽样判定并记录证据（截图/抽样表） |

> **确定性优先**：覆盖率、引用可核对、无证据短路、**Loop 边界（步数/终止/工具白名单/轨迹）**、结构化合法性等**全部程序判定**（Loop 用 mock 驱动）；仅"建议是否合理"用人工抽样，**不引入 LLM-as-judge**。

## 1. 功能验收（判定型）

| 编号 | 标准 | 判定 | 阈值 / 断言 | 证据 |
| --- | --- | --- | --- | --- |
| H1 | 审查能力健康 | **自动** | `/api/reviewadvisor/health` 返回 `capabilities.advisor=true`（`llm` 反映模型是否配置） | 脚本输出 |
| R0 | 生成并流转 | **自动** | `POST /api/review/runs/{id}/advisor` 返回 202；轮询终态 `succeeded` | 脚本输出 |
| A1 | 覆盖所有问题项 | **自动** | 结论 `finding_id` 集合 **=** 该 run 的 findings 集合（覆盖率 100%） | 脚本输出 |
| A2 | 引用可核对 | **自动** | 每条 `concluded`：`chunk_id` 非空、`seq` ∈ 文档真实 chunks、`evidence` 命中该 chunk | 脚本输出 |
| A3 | 无证据不下结论 | **自动** | 无合格证据 → `status=insufficient`、`basis=no-evidence`；**不得**出现无证据的 `concluded` | 脚本输出 |
| A4 | 无证据短路先于模型 | **自动（mock）** | 无证据 finding：断定**模型未被调用**（mock 记录调用次数=0） | 脚本输出 |
| A5 | 引用后校验 | **自动（mock）** | mock 返回无效引用 → 该条降级 `insufficient`（不落脏结论） | 脚本输出 |
| S1 | 结构化输出合法 | **自动** | `conclusions` 可按 DTO 反序列化；`status/basis/title` 完整；无解析错误 | 脚本输出 |
| D1 | 删除级联 | **自动** | 删文档后 `review_runs` / `findings` / `conclusions` / `review_agent_steps` 全消失 | 脚本输出 |
| RR1 | 重跑幂等 | **自动** | 重跑后终态 `succeeded`，结论与轨迹按 run 重建（行数不翻倍） | 脚本输出 |
| AL1 | 不超最大步数 | **自动（mock）** | 每条 finding 轨迹步数 ≤ `maxSteps`（默认 6） | 脚本输出 |
| AL2 | 必定终止 | **自动（mock）** | mock 诱导死循环 → 到 `maxSteps`/`timeout` 终止，run **不挂起** | 脚本输出 |
| AL3 | 工具白名单 | **自动（mock）** | 轨迹中不出现白名单外工具；越权调用被拒且留痕 | 脚本输出 |
| AL4 | 轨迹完整 | **自动** | 每步含 `kind/tool/result`；结论 `steps_used` 与轨迹步数一致 | 脚本输出 |
| L1 | LLM 增强结论 | **半自动** | 配模型后 `source=llm` 结论出现（未配模型 SKIP） | 抽样记录（未配 SKIP） |

## 2. 度量验收

| 编号 | 标准 | 判定 | 阈值 | 证据 |
| --- | --- | --- | --- | --- |
| M1 | 结论覆盖率 | **度量** | findings 中有结论的比例 **= 100%** | `eval-p3` 报告 |
| M2 | 引用可核对率 | **度量** | `concluded` 中引用可核对的比例 **= 100%** | `eval-p3` 报告 |
| M3 | 无证据不结论率 | **度量** | 无证据项标 `insufficient` 的比例 **= 100%** | `eval-p3` 报告 |
| Q1 | 建议合理率 | **人工** | 抽样 **≥80%** 补正建议合理、未越界 | 抽样表 |

> 度量均为**程序计算**（对照小规模金标 `tests/benchmark/p3/golden/`），不调用模型做判断。

## 3. 负控（红队）——必须存在，且必须"会失败"

| 编号 | 负控输入 | 判定 | 期望结果 |
| --- | --- | --- | --- |
| N1 | 无 findings 的 run / 无关文档 | **自动** | 不产出编造结论（空或全 `insufficient`），无"有结论却无证据" |
| N2 | 未配置模型 | **自动** | `/health` `advisor=false`；`POST advisor` 明确 4xx 或 run `failed` 且 `error` 非空，**不挂起** |
| N3 | 非法 `runId` | **自动** | `POST /runs/{id}/advisor` 返回 `404` |
| N4 | 未完成（pending/running）或 failed 的 run | **自动** | `POST advisor` 返回 `400` |
| N5 | LLM 返回非 JSON（**mock**） | **自动** | 该条降级 `insufficient` / 生成失败；**不写脏数据** |
| N6 | 模型诱导死循环（**mock**） | **自动** | 到 `maxSteps`/`timeout` 终止（AL2 复验），不挂起 |
| N7 | 模型请求白名单外工具（**mock**） | **自动** | 工具被拒；轨迹记录该次；不执行、不越权 |

> N5/N6/N7 一律用**本地 mock**，不调用真实模型。

## 4. 测试集

- 复用 P2：`tests/corpus/`、`tests/benchmark/p2/negative/`（缺条款、金额不一致）。
- 新增 `tests/benchmark/p3/`：
  - `golden/`：≥1 份合同的 `conclusions.json` 金标（finding → 期望 `status`/是否需引用），供 M1/M2/M3。
  - `no-evidence/`：1 份"有问题但检索不到可核对证据"的样本，供 A3/A4/N1。
- 本地 mock：`tests/fixtures/llm/`（固定响应：正常 JSON / 非 JSON / 死循环 / 越权工具），**不得调用真模型**。

## 5. 自动化脚本

```powershell
# 判定 + 负控（默认：构建 → 清库 → 起服务 → 跑 → 停服务）
powershell -ExecutionPolicy Bypass -File scripts/acceptance-p3.ps1
# 指标（覆盖率 / 引用可核对率 / 无证据不结论率）
powershell -ExecutionPolicy Bypass -File scripts/eval-p3.ps1
```

- 断言：H1/R0/A1/A2/A3/A4/A5/S1/D1/RR1/AL1/AL2/AL3/AL4 + N1/N2/N3/N4/N5/N6/N7。
- Loop 相关（AL*/N5/N6/N7）用 **mock** 确定性判定，不依赖真模型；L1 未配模型 `SKIP`。
- 退出码：**确定性断言全过 0；有失败 1**。
- 输出：每个断言 PASS/FAIL/SKIP 与明细；`eval-p3` 输出 `tests/benchmark/p3/report.json`。

## 6. 人工抽样表（随验收报告填写）

| 样本 | 文档 | findings 数 | 结论数 | 覆盖率 | 引用可核对 | 建议合理 | 越界（有/无） | 问题摘要 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |  |  |  |

## 7. 放行判定

- **自动**：H1/R0/A1/A2/A3/A4/A5/S1/D1/RR1/AL1/AL2/AL3/AL4 + N1~N7 全 PASS（L1 允许 SKIP）。
- **度量**：M1/M2/M3 达标。
- **人工**：Q1 抽样完成且 ≥80%。
- 三者同时满足 → **P3 通过**。

## 8. 与总验收（指南里程碑）的对应

本仓库把指南 P3 重新编排：**P3 = Agent Loop（审查 Agent）／P4 = Workflow Loop／P5 权限与审计／P6 评测平台化 + 兜底**。

- 审查 Agent（Agent Loop）：本表 A1–A5 / AL1–AL4 / M1–M3 / Q1。
- 工作流编排（步骤/单步重跑/提示词版本）：P4，不在本表。
- 登录/角色/审计：P5。
- 评测平台化 + 兜底完善：P6。

> 顺延关系已在 `docs/测试标准/落地清单.md` 同步。

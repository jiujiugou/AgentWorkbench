# MAF 判定轨测试方案（AgentWorkbench）

> 版本：v1.0 ｜ 日期：2026-10-13
> 依据：`docs/测试标准/Agent-测试与验收标准.md`（下称《标准》）**§1 铁律 / §2 分层 / §3 判定型 / §5 可复现 / §6 门禁**
> 上游：`docs/测试标准/评测总体方案.md`（三轨分工与阶段路线）、`docs/测试标准/评测实现指南.md`（六域矩阵与 U1–U7 未决项）
> 事实来源：`Microsoft.Agents.AI` 1.23.0 程序集反射实测（本机 NuGet 缓存）+ `src/` 源码行号取证
>
> **本文件只描述「测什么、怎么测、为什么这么测」**，不含实测结果（结果另见 §8，跑一批填一批）。

---

## 0. 一句话结论

> **MAF 判定轨评的是「管道有没有退化」，不是「Agent 聪不聪明」。**
> 判据全部自建、布尔可判定、零 LLM；MAF 只出三样东西：**批量驱动**（`LocalEvaluator` + `EvalChecks`）、**断言容器**（`AssertAllPassed`）、**判分结果的聚合**。
> 因此本轨是**唯一可以进 CI 门禁**的轨——退出码非 0 即阻断。

> **⚠️ 2026-10-14 更正**：上面「只出三样」是**用到的**三样，不是 MAF **拥有**的全部。
> 反射实测该子系统共 **28 个公开类型 / 6 个功能族**（预期数据通道、会话切分、循环终止评估、MEA 适配器……），
> 其中 4 族本轨一个类型都没用；另有 6 个字段在本地路径下为 `null`、2 个 `Assert*` 是 no-op。
> 完整盘查与实测证据见 **`docs/测试标准/MAF评估能力盘查.md`**。

---

## 1. 这一轨在整体评测里的位置

| 轨 | 工具 | 评什么 | 判分权 | 门禁 |
|---|---|---|---|---|
| **判定轨（本文）** | MAF Evaluation | 管道行为：工具白名单、循环终止、预算、超时≠取消、取消≠失败、落账 | **我（自建）** | ✅ 进 CI |
| 认证轨 | τ²-bench | Agent 能不能在多轮交互后把环境状态改对 | **τ² 的 Python 侧** | ❌（外部裁判，趋势） |
| 质量轨 | Ragas（**暂不引入**） | 检索召回 / 回答依据 | LLM-as-judge | ❌（撞《标准》§1.1、§6） |

**为什么判定轨排第一**：它评「**失败路径是红的**」，τ² 评「**成功路径是绿的**」。给一条没测过失败路径的 harness 发能力认证，`reward = 1.0` 无法归因——分不清是"真办成了"还是"恰好没踩坑"。

> 实证：P2 负控（并发 run 用量串扰）**首版假绿过**。原因是预算检查挂在 `AgentRunGuard.InspectToolCall`（`src/AgentWorkbench.AgentLoop/AgentRunGuard.cs:36`），只在**模型发出工具调用被检查时**判预算；我的场景第二轮返回文本，检查根本不触发。修正场景（第二轮必须再发工具调用）后它才真红。

---

## 2. 被测对象与判定流水线

### 2.1 被测的是什么

`IAgentHarness`（`src/AgentWorkbench.AgentHarness/AgentHarnessService.cs`）一次 run 的**编排行为**：
解析模型 → 跑工具环 → 施加执行控制（迭代/预算/超时/重复检测）→ **无论什么结局都落账本** → 写回会话。

### 2.2 流水线（一次判定的完整形状）

```
ScriptedChatClient（按请求序号回放，可抛异常）
   └→ AgentHarnessService（生产同一构造路径装配）
        ├→ ToolExecutionPipeline（守卫判定发生在工具真正执行之前，ToolExecutionPipeline.cs:103）
        └→ AgentLoopService（run 级超时 → AgentRunTimeoutException，AgentLoopService.cs:34）
             ↓
       HarnessRunResult / 抛出的异常
             ↓
       RecordingTraceStore（内存账本，可注入写失败）
             ↓
       EvalCase(query, AgentResponse, LedgerFact)         ← 三者按 query 对齐
             ↓
       EvalCheck[]（自建：EvalChecks.* + LedgerChecks.*）
             ↓
       new LocalEvaluator(checks) → EvaluateAsync(agent, responses, queries, …)   ← 回放轨，不重跑
             ↓
       AgentEvaluationResults.AssertAllPassed(...)        ← 失败即抛 → dotnet test 退出码非 0
```

### 2.3 为什么判分对象是「账本」而不是「结果对象」

两条硬理由：

1. **失败 / 取消 / 超时三条路径没有结果对象**——只有「抛了什么」和「账本留下了什么」。
2. **「跑满迭代被强制收口」与「模型答完了」在对话上长得一模一样**（框架跑满时不报错，而是清空工具逼模型用文本收口再正常返回）。只有账本的 `status` / `error_code` 能分开——这正是本仓库 `AgentRunStatus` + `AgentRunCode` 双字段存在的意义。

**技术缺口与解法**：`EvalItem` 只暴露 `Query` / `Response` / `Conversation`（反射实测确认，**没有 index、没有账本**）。所以自建判据用 `EvalItem.Query` 把样本映射回本轮落库的事实（`HarnessUnderTest.Ledger`）——这就是 `Checks/LedgerChecks.cs` 存在的唯一原因。

---

## 3. 测试清单（域 3 执行控制 + 域 6 账本）

判据证据列一律指向 `文件:行`。**无证据不得判 PASS**（《标准》§1.2）。
`判定列`：**M** = 过 MAF 容器（`AssertAllPassed` 门禁）；**D** = 直接断言（作为 M 的前置/补充）。

### 3.1 域 3｜执行控制 Agent Loop / Harness

| # | 不变量（可证伪） | 正例（绿） | 负控（必须红/必须不同） | 判定 | 证据（src） |
|---|---|---|---|---|---|
| 3.1 | 同工具+同参数连续 `MaxRepeatedToolCalls` 次 → `LoopDetected`，**干净收口**（不入 MEA 失败路径） | `harness-loop` 场景：status=loop_detected，第 3 次调用在执行前被拦 | 同一脚本关掉检测（`=0`）→ 结局必须变 `iteration_limit` | M | `AgentRunGuard.cs:60` `DetectLoop`、`ToolExecutionPipeline.cs:110` `Terminate=true` |
| 3.2 | token 预算：`当前用量 − 构造基线 > MaxTokensPerRun` → `BudgetExceeded`，**拦在执行之前** | 100 token/请求、预算 50 → budget_exceeded，工具步 ≤ 1 | 同一脚本关掉预算（`=0`）→ 结局变 `iteration_limit`（证明旋钮起效，不是脚本本身坏） | M | `AgentRunGuard.cs:82` `DetectBudgetOverage`、调用点 `ToolExecutionPipeline.cs:103` |
| 3.3 | `InspectCompletion`：最后工具轮次顶到 `MaxIterations-1` → `IterationLimit`，**与"模型答完了"必须可区分** | 跑满轮次 → `iteration_limit` | 同 `MaxIterations`、末轮改答文本 → `succeeded` 且 Code 为 null；**两者 Status 必须不同** | M | `AgentRunGuard.cs:45` `InspectCompletion` |
| 3.4 | **run 级超时** → `AgentRunTimeoutException`（**≠** 通用 `TimeoutException`）→ 账本 `timeout` / `run_timeout` | `TimeoutSeconds=1` + hang 工具 | 同样脚本，超时放宽 → `succeeded`（证明是旋钮起效） | M | `AgentLoopService.cs:34`、`AgentHarnessService.cs:200` |
| 3.4b | **单次模型请求超时** → 通用 `TimeoutException` → 账本 `timeout` / `model_timeout`，**不得被归成 run_timeout** | 模型运行时替身直接抛 `TimeoutException` | 断言异常类型**不是** `AgentRunTimeoutException`（二者是继承关系，最容易混） | M + D | `AgentHarnessService.cs:209`（catch 顺序）、`IAgentLoop.cs:32`（继承 `TimeoutException`） |
| 3.5 | 调用方取消 → `Cancelled`（**≠** `Failed`），且取消真的穿进工具内部 | 200ms 后取消 → `cancelled`/`cancelled` | 不取消、工具快速返回 → `succeeded`；取消穿透标记必须为假 | M | `AgentHarnessService.cs:191` |
| 3.6 | **失败 / 取消 / 超时 / 成功四条路径都必须落账本** | 四路径各跑一次 → 账本四行且 status 互不相同 | 只在成功分支落库的回归（历史陷阱） | M（**批量驱动的主战场**） | `AgentHarnessService.cs:175`、`:195`、`:204`、`:220` |
| 3.7 | 会话写回 best-effort，**绝不因调用方取消而丢写**（传 `CancellationToken.None`） | 取消后断言 `Sessions.Saves` 有记录且令牌**不可取消** | 传原令牌（取消后写回被跳过） | D | `AgentHarnessService.cs:366` `SaveSessionQuietlyAsync` |
| 3.8 | 空答复**不落** assistant 步（否则"没有答复"被写成"答复为空"） | LoopDetected 场景 → 步骤里无 assistant | 正常收口场景 → 必须有 assistant 步（对照，证明检查有区分度） | M | `AgentHarnessService.cs:349` |
| 3.9 | 失败分类：工具异常 → `ToolError`；其余 → `Unhandled`（实例引用优先，退化为类型+消息） | 工具连续失败 → `failed`/`tool_error` | 模型侧抛非工具异常 → `failed`/`unhandled_error`（**两者 Code 必须不同**） | M | `AgentHarnessService.cs:299` `ClassifyFailure` |
| 3.10 | 落库/会话写回自身出错**不得掩盖**原始异常 | 注入账本写失败 + 工具失败 → 抛出的仍是**工具异常** | 让落库异常外泄（掩盖原始异常） | M + D | `AgentHarnessService.cs:267` `PersistQuietlyAsync` |

> **P2 已知偏差（并发用量串扰）**：不单列不变量，作为**常设负控**存在
> （`UsageIsolationNegativeControls.cs`）——它断言"缺陷存在"，按用户拍板**保持红以锚定修复**；
> 缺陷修好后该测试转红（失败）即提示"负控已完成使命，可改为正例"。
> 证据：`AgentRunGuard.cs:32`（构造时取基线）+ `RuntimeUsageSource`（进程级单例计量器）。

### 3.2 域 6｜可观测与评估（账本）

| # | 不变量 | 正例 | 负控 | 判定 | 证据 |
|---|---|---|---|---|---|
| 6.1 | 每次 run 必有 `agent_runs` 行（四条路径全覆盖） | 同 3.6 的四路径批量 | 只在成功路径落库 | M | `AgentHarnessService.cs:175/195/204/220` |
| 6.3 | 轨迹可回放：`GetRunAsync(runId)` 复原 status / code / steps | 落库后取回比对 | 丢掉 `ErrorCode` | D | `AgentHarnessService.cs:234` |
| 6.4 | 失败时 `ErrorCode` 非空 | `ToolError` 场景 | 置 `null`（只留人话） | M | `AgentRunCode`（`AgentWorkbench.Shared`） |
| 6.5 | assistant 步截断到 `MaxAssistantChars`(=400) | 500 字答复 → 落库 400 字 | 不截断 | M | `AgentHarnessService.cs:30`、`:363` |
| 6.2 | `agent_steps.Iteration` 单调不减 | 多轮 run | 乱序插入 | D（随 6.1 附带断言） | `ToTraceSteps` `:358` |

**不在本轨范围**：域 1（模型交互，需 MockHttp 探针）、域 2（工具调用白名单）、域 4（记忆）、域 5（安全，真空缺需先定契约）——排阶段 2，见 §7。

---

## 4. 目录与文件

```
tests/AgentWorkbench.Evaluation.AgentTask/
  Fixtures/
    ModelFakes.cs            ScriptedChatClient（按序号回放，可抛异常）/
                             PassthroughModelRuntime / MeteredModelRuntime + UsageMeter（可共享，复现串扰）/
                             ThrowingModelRuntime（抛固定异常 → 走 model_timeout 路径）
    TestTools.cs             FaultInjectorTool（throw/hang/delay + HangGate 闸门 + 取消穿透标记）/ FakeToolCatalog
    HarnessUnderTest.cs      装配 + HarnessOutcome/HarnessFailure + LedgerFact/EvalCase +
                             RecordingTraceStore（可注入写失败）/ RecordingSessionStore（记录写回令牌）/
                             RunAsync / RunExpectingFailureAsync / EvaluateAsync（批量判分）
  Checks/
    LedgerChecks.cs          自建判定原语：StatusIs / ErrorCodeIs / ErrorCodeNonEmpty /
                             WasPersisted / NoAssistantStep / AssistantStepWithin /
                             ToolStepsAtMost / ToolStepErrorsAtLeast
  Domain3_AgentLoop/
    Domain3InvariantTests.cs           容器落地 + 检查区分度负控（已实现）
    UsageIsolationNegativeControls.cs  P2 并发串扰负控（已实现）
    BudgetInvariants.cs                3.2（待实现）
    TimeoutInvariants.cs               3.4 / 3.4b（待实现）
    TerminationInvariants.cs           3.3 / 3.5 / 3.7 / 3.8 / 3.9 / 3.10（待实现）
  Domain6_Ledger/
    LedgerInvariants.cs                6.1 四路径批量 / 6.2 / 6.3 / 6.4 / 6.5（待实现）
```

**依赖**：xunit 三件套 + Moq + 一个 `ProjectReference`（`AgentHarness`）。
**判定轨 0 个评测专用 NuGet 包**——`Microsoft.Agents.AI` 1.23.0 的 nuspec 依赖 `Microsoft.Extensions.AI.Evaluation`，评测类型经传递依赖到手（已核实，`Directory.Packages.props` 无需新增条目）。

---

## 5. 隔离与可复现（《标准》§5）

| 关注点 | 做法 |
|---|---|
| 模型 | 进程内脚本化客户端，**零 HTTP、零 API key**；脚本按请求序号回放，也可直接抛异常 |
| 账本 | 内存 `IExecutionTraceStore`，不落盘；可注入写失败 |
| 会话 | 内存 `SessionStore`（或探针包装），不落盘 |
| 并发 | `HangGate`（`TaskCompletionSource`）+ 轮询等待条件，**确定性排序**而非 `Task.Delay` 竞猜 |
| 时间 | run 级超时用 `TimeoutSeconds=1`（唯一需要等真实时间的场景，约 1.2s） |
| 判据 | 全部同步布尔判定，`numRepetitions=1`，无随机、无 LLM |

---

## 6. 命令与门禁

```bash
dotnet build AgentWorkbench.slnx
dotnet test tests/AgentWorkbench.Evaluation.AgentTask      # 退出码 0 = 通过；非 0 = 阻断
```

**门禁牙齿的验证**（每条轨必须做一次）：把期望值换成对立值，`AssertAllPassed` **必须抛**。
已在 `DegradedNoTool_FailsToolCalledCheck_NegativeControl` 里验证过一次——否则"全绿"可能只是门禁是空的。

**前置**：跑测试前停掉常驻 Webapi（否则锁 `bin` 报 MSB3027）。

---

## 7. 执行顺序与完成判据

| 阶段 | 内容 | 完成判据（不是"跑通了"） |
|---|---|---|
| **1a** ✅ 已完成 | 容器落地 + 检查区分度负控 + 3.1 正负控对 + P2 负控 | 6/6 通过；负控逐条确认能红；`AssertAllPassed` 有牙（已验证会抛） |
| **1b** 下一步 | 域 3 剩余：3.2 / 3.3 / 3.4 / 3.4b / 3.5 / 3.7 / 3.8 / 3.9 / 3.10 | 十条不变量**每条都有正负控对**；把任一旋钮关掉，对应检查必须红 |
| **1c** | 域 6 账本：6.1 四路径批量 / 6.2 / 6.3 / 6.4 / 6.5 | 四条路径一次性进同一个 `EvaluateAsync` 批量判分（本轨"批量驱动"价值的实证） |
| 2 | 域 1（模型交互）+ 域 2（工具白名单），含 **P1 闸门负控** | 需 MockHttp 探针；1.1 与 1.2 互斥对必须同时成立 |
| 3 | 趋势轨：`Reporting` + `report.json` 基线对比 | **不进门禁**（+1 个 NuGet 包，单独评估） |

---

## 8. 结果台账（跑一批填一批）

| 不变量 | 测试名 | 类型 | 结果 |
|---|---|---|---|
| 3.3（答完 vs 跑满，容器侧） | `ToolLoop_PassesMafContainer_AndClassifiesSucceeded` | 正例 | ✅ 通过 |
| 检查区分度 | `DegradedNoTool_FailsToolCalledCheck_NegativeControl` | 负控 | ✅ 通过（含 `AssertAllPassed` 有牙验证） |
| 3.1 | `RepeatedIdenticalCalls_StopAsLoopDetected` | 正例 | ✅ 通过 |
| 3.1 | `SameScript_LoopDetectionDisabled_RunsToIterationLimit` | 负控 | ✅ 通过 |
| 3.2 | `SingleRun_UnderBudget_Succeeds_PositiveControl` | 正例 | ✅ 通过 |
| P2 | `ConcurrentRun_BudgetIsPollutedByOtherRunsUsage_NegativeControl` | 负控 | ✅ 通过（断言缺陷存在，锚定修复） |
| 3.2 – 3.10 / 6.1 – 6.5 | 见 §4 待实现文件 | — | ⏳ 待实现 |

---

## 9. 诚实边界（别拿绿灯做错误结论）

| 绿灯 | 代表 | **不代表** |
|---|---|---|
| 判定轨全绿 | 管道没退化：工具白名单生效、循环能终止、预算能拦、超时≠取消、落账完整 | ❌ Agent 聪明 ❌ 回答质量好 ❌ 检索准 |
| 负控能红 | 检测器真的在起作用 | ❌ 缺陷已修（负控恰恰是证明缺陷**还在**） |
| P2 负控通过 | 串扰缺陷被稳定复现 | ❌ 串扰已修（按用户拍板 src 不动，它锚定的是修复点） |

全仓库**无可用 API key** 是客观事实——现在拿不到任何"模型决策质量"的结论。写清楚比含糊过去好。

---

## 10. 非目标

- **不改 `src/` 一行**（评测是观测，不是能力；《评测实现指南》§10）
- **不采用 MAF 的 12 个 Quality 裁判**（全需 LLM judge，撞《标准》§1.1 第 3 档与 §6「AI 不得作为门禁」）
- **不替代 xunit**（`EvaluateAsync` 是被调用的库，不是测试运行器）
- **不重写 `EvalChecks.*` 已有原语**（只在其看不见账本时自建 `LedgerChecks`）
- **不做 `LoopAgent` 替换实验**（《指南》§8 要求先出实验数据）

---

## 11. 与其他文档的关系

| 文档 | 分工 |
|---|---|
| `Agent-测试与验收标准.md` | 铁律与分层（§1/§2/§3/§5/§6）——本文件是它的落地 |
| `评测总体方案.md` | 三轨分工 + 阶段 0–5 路线——本文件是其中「判定轨」的执行细则 |
| `评测实现指南.md` | 六域矩阵（A 不变量 / B 度量 / C 偏差）+ U1–U7——本文件的 §3 清单是它 §3 域 3/域 6 的可执行化 |
| 本文件 | 判定轨的**测试清单、流水线、判据、完成判据** |

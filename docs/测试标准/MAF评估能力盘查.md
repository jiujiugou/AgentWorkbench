# MAF 评估能力盘查（AgentWorkbench）

> 日期：2026-10-14 ｜ 版本：v1.0  
> 事实来源：`Microsoft.Agents.AI` 1.23.0 的 `Microsoft.Agents.AI.xml` 官方文档条目全量提取
>
> - 本机**实测探针**（临时 xUnit 探针，跑完已删）。  
>   目的：回答一个问题——**MAF 的 Evaluation 子系统里，哪些能力是现成的、我不用再造轮子。**



---

## 0. 一句话结论

> MAF 的评估面**远不止**我们文档里写的「批量驱动 + 断言容器 + 聚合」三样——它实际有 **28 个公开类型**，  
> 分成 **6 个功能族**。其中**4 族我们一个类型都没用**。  
> 但另一半同样重要：**6 个字段/方法在本地方案下是空的或恒真**，拿它当门禁会得到假绿。



---

## 1. 能力全景（28 个类型，6 族）

### 族 A｜批量驱动与聚合 ← *已用*

| 类型                               | 关键成员                                                                                             | 我们用了没                    |
| -------------------------------- | ------------------------------------------------------------------------------------------------ | ------------------------ |
| `AgentEvaluationExtensions`      | 5 个 `EvaluateAsync` 重载（回放轨 ×2 / 驱动轨 ×3）                                                          | ✅ 用了 2 个                 |
| `AgentEvaluationResults`         | `Total` / `Passed` / `Failed` / `AllPassed` / `Items` / `InputItems`                             | ✅ 用了 3 个属性               |
| `AgentEvaluationResults.Assert*` | `AssertAllPassed` / `AssertNoFailedItems` / `AssertScoreAtLeast` / `AssertDimensionScoreAtLeast` | ⚠️ 只用了 `AssertAllPassed` |

### 族 B｜判据原语 ← *只用了 2/7*

| 类型                  | 成员                                                                                                                  | 我们用了没         |
| ------------------- | ------------------------------------------------------------------------------------------------------------------- | ------------- |
| `EvalCheck`         | `delegate EvalCheckResult (EvalItem)`                                                                               | ✅             |
| `EvalCheckResult`   | `(Passed, Reason, CheckName)`                                                                                       | ✅             |
| `EvalChecks`        | `ContainsExpected(bool)`                                                                                            | ❌             |
|                     | `KeywordCheck(params string[])` / `KeywordCheck(bool, ...)`                                                         | ❌             |
|                     | `ToolCalledCheck(string[])` / `ToolCalledCheck(ToolCalledMode, string[])`                                           | ✅             |
|                     | `ToolCallsPresent`                                                                                                  | ❌             |
|                     | `ToolCallArgsMatch`                                                                                                 | ❌             |
|                     | `NonEmpty(int)`                                                                                                     | ✅             |
|                     | `HasImageContent`                                                                                                   | ❌             |
| `ToolCalledMode`    | `All` / `Any`                                                                                                       | ❌             |
| `FunctionEvaluator` | `Create(name, Func<string,bool>)`、`Func<string,string,bool>`、`Func<EvalItem,bool>`、`Func<EvalItem,EvalCheckResult>` | ❌ 我们手写 lambda |

### 族 C｜评估器与适配 ← *用了 1/4*

| 类型                      | 说明                                                                 | 我们用了没 |
| ----------------------- | ------------------------------------------------------------------ | ----- |
| `IAgentEvaluator`       | `EvaluateAsync(IReadOnlyList<EvalItem>, evalName, ct)` — **公开可直调** | 间接    |
| `LocalEvaluator`        | `ctor(EvalCheck[])`，本地零 API 调用                                     | ✅     |
| `MeaiEvaluatorAdapter`  | **把 MEA 的 `IEvaluator` 包成 `IAgentEvaluator`** — 两库合流的桥             | ❌     |
| `GeneratedEvaluatorRef` | 引用 Foundry 门户生成的 rubric 评估器                                        | ❌（需云） |

### 族 D｜预期数据通道 ← *一个都没用*

把「正确答案」作为**数据**喂进去，而不是写成判据函数。这是 MAF 最容易被忽略的减法。

| 类型 / 参数                                        | 说明                                                  |
| ---------------------------------------------- | --------------------------------------------------- |
| `ExpectedToolCall(Name, Arguments)`            | 期望的工具调用（`Arguments=null` 表示不校验参数；提供了则做**子集匹配**）     |
| `EvaluateAsync` 的 `expectedOutput` 参数          | 每条 query 一个金标 → 打到 `EvalItem.ExpectedOutput`        |
| `EvaluateAsync` 的 `expectedToolCalls` 参数       | 每条 query 一组期望工具调用 → 打到 `EvalItem.ExpectedToolCalls` |
| `EvalItem.Context` / `.Tools` / `.RawResponse` | 接地上下文 / 可用工具 / 原始 ChatResponse                      |

### 族 E｜多轮与会话切分 ← *一个都没用*

| 类型                               | 说明                                                        |
| -------------------------------- | --------------------------------------------------------- |
| `IConversationSplitter`          | `Split(conversation) → (queryMessages, responseMessages)` |
| `ConversationSplitters.LastTurn` | 只在最后一轮问答上判分（默认）                                           |
| `ConversationSplitters.Full`     | **把整条会话轨迹当一个判分对象** — 「整条任务是否为原始请求服务」的现成载体                 |

### 族 F｜循环终止评估 ← *一个都没用（也最意外）*

这是一整套「Agent 循环该不该再来一轮」的框架，与我们的 `AgentLoopService` 是**外层 vs 内层**的关系。

| 类型                                      | 说明                                                                             |
| --------------------------------------- | ------------------------------------------------------------------------------ |
| `LoopAgent`                             | 包装一个 `AIAgent`，按 `LoopEvaluator` 的判决反复重跑；`MaxIterations`                       |
| `LoopEvaluator`                         | 抽象基类：`EvaluateAsync(LoopContext, ct) → LoopEvaluation`（纯判断，无状态）                |
| `LoopEvaluation`                        | `Stop` / `Continue(feedback)` / `ContinueWithMessages(messages)`               |
| `LoopContext`                           | `Iteration` / `LastResponse` / `Feedback` / `Session` / `AdditionalProperties` |
| `CompletionMarkerLoopEvaluator`         | 看到「完成标记」才停                                                                     |
| `TodoCompletionLoopEvaluator`           | 待办清单全部完成才停（`Modes` 可配）                                                         |
| `BackgroundTaskCompletionLoopEvaluator` | 后台任务清零才停                                                                       |
| `AIJudgeLoopEvaluator`                  | LLM 裁判判「还要不要继续」（**撞《标准》§1.1，不用**）                                              |
| `DelegateLoopEvaluator`                 | 委托自定义 `Func<LoopContext, ct, ValueTask<LoopEvaluation>>`                       |

---

## 2. 落到「五层」

| 层                     | MAF 提供什么                                                                                                                | 我**不用造**什么                                                                                                  | 我**仍得造**什么                                                                   |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| **单元**                | 无（也不该用）                                                                                                                 | —                                                                                                           | 全替身装配 + 算法断言（现状健康）                                                           |
| **集成 / AgentTask**    | 族 A + B + C + D                                                                                                         | 批量循环、`Passed/Failed` 计数、check 结果→度量封装、失败信息结构（`Reason`/`CheckName`）、样本↔输入的审计对齐（`Items[i]` ↔ `InputItems[i]`） | **判据语义**（账本 `status`/`error_code` 是我们的领域知识）、`EvalItem`→账本的映射（`LedgerChecks`） |
| **端到端**               | 驱动轨 `EvaluateAsync(agent, queries, …)` + `ConversationSplitters.Full`                                                   | 跨批次组织、整轨迹切分                                                                                                 | **真 HTTP 入口**（`WebApplicationFactory`）——MAF 完全不覆盖，这层仍为空                      |
| **质量 / ModelQuality** | `IEvaluator` 直连重载、`MeaiEvaluatorAdapter`、`BLEU/GLEU/F1`（非 LLM）、`CompositeEvaluator`、`EvaluationRating`/`Interpretation` | 度量组合、分数→评级、度量与判定的容器对接                                                                                       | 字段级 P/R/F1（自算更准）、门禁出口                                                        |
| **横切**                | `numRepetitions`（同一 query 重复 N 次，`Total = N × queries.Count`）＝**一致性/稳定性度量现成**                                           | 重复跑的循环                                                                                                      | **并发时序编排**（`HangGate`）；**架构扫描**（`ArchitectureTests` 与 MAF 无关）                |

---

## 3. 实测：本地评测器路径下，结果字段到底填了什么

探针条件：`LocalEvaluator(checks)` + 回放轨 + 2 条样本，`MaxIterations=10`。  
原始输出（节选）：

```
Total=2 Passed=2 Failed=0 AllPassed=True
Status=<null>  Error=<null>
ProviderName=LocalEvaluator  ReportUrl=<null>   EvalId=<null>  RunId=<null>
Items=2  InputItems=2  SubResults=<null>  DetailedItems=<null>  PerEvaluator=<null>
  Item: EvaluationResult  Metrics=1
    Metric: ledger.persisted=True Rating=Good Failed=False Reason=账本里必须留下这次 run
```

### 3.1 可用（本地方案下真实填值）

| 字段                                                           | 实测值                                                             | 用途     |
| ------------------------------------------------------------ | --------------------------------------------------------------- | ------ |
| `Total` / `Passed` / `Failed` / `AllPassed`                  | 2 / 2 / 0 / True                                                | 门禁     |
| `Items`                                                      | 2 个 `EvaluationResult`（MEA 类型）                                  | 逐条下钻   |
| `Items[i].Metrics`                                           | `IDictionary<string, EvaluationMetric>`，键 = **我们的 `CheckName`** | 逐条看判据  |
| `Metric.Interpretation.Rating` / `.Failed` / `Metric.Reason` | `Good` / `False` / 我们的 `Reason` 原文                              | 失败原因   |
| `InputItems`                                                 | 2 项，含 `Query`/`Response`/`Conversation`                         | 审计与定位  |
| `ProviderName`                                               | `"LocalEvaluator"`                                              | 结果来源标注 |

> **结论**：`LocalEvaluator` 已经替我们做了「check → metric → 评级(Good) → 原因」这一整条封装。  
> 我们**不需要**再自己写「判据结果 → 汇报对象」的转换层。

### 3.2 本地方案下实测为 `null` 的字段

| 字段                               | 文档原文                                         | 本地为 null 的原因                 |
| -------------------------------- | -------------------------------------------- | ---------------------------- |
| `ReportUrl` / `EvalId` / `RunId` | "Foundry only"                               | Foundry provider 未接入        |
| `PerEvaluator`                   | "Foundry only"                               | 同上                           |
| `DetailedItems`                  | "from the Foundry output_items API"          | 同上（⇒ 分数类断言随之失效，见 §3.3）       |
| `Status` / `Error`               | （**无** Foundry 标注，通用语义）                      | 本地评测器不产出「运行态」                |
| `SubResults`                     | "per-agent results for **workflow** evaluations" | **只有工作流评估才产这个字段**，非 Foundry-only（见 §10 第 5 条） |

> **原本文把 `SubResults` 与 `Status`/`Error` 一起归进「Foundry-only」是**错的**（2026-10-14 更正）**：
> `SubResults` 的文档明写是**工作流评估的按 Agent 分层结果**，回放轨（纯 Agent、非工作流）下没有它才正常。

### 3.3 ⚠️ 实测确认的**失效**能力

```
AssertScoreAtLeast(999.0)  → 不抛
```

**`AssertScoreAtLeast` / `AssertDimensionScoreAtLeast` 在本地路径下是 no-op。**  
两条 `Assert` 都走 `DetailedItems`，而它是 `null`；文档明写  
"When `DetailedItems` is `null`, the assertion is a **no-op** for this level"。  
→ 想要分数门禁，**只能自己做断言**（对 MEA 的 `NumericMetric` 直接比）。  
→ 只有 `AssertAllPassed` / `AssertNoFailedItems` 在本地方案下有牙。

---

## 4. 两个实测发现的真问题

### 4.1 `EvalItem.Response` 不是「Agent 的答复」，是整条文本的拼接

```
我方喂入的 AgentResponse： [0] user "QUESTION"  [1] assistant "ANSWER"
                      Text = "QUESTION\r\nANSWER"

MAF 回填的 EvalItem：  Query    = "QUESTION"
                      Response = "QUESTION\r\nANSWER"     ← 含 query
                      Conversation = [user "QUESTION", user "QUESTION", assistant "ANSWER"]  ← user 重复
```

两个后果：

1. **文本类判据被 query 污染。**`EvalChecks.NonEmpty` 读的正是 `Response`  
   （实测：`new EvalItem("ONLY_QUERY", "")` → `Passed=False, Reason="Response length 0 is below minimum 1"`）。  
   在回放轨下 `Response` 永远含非空 query → **`NonEmpty(1)` 恒真，零区分度**。  
   `ContainsExpected` / `KeywordCheck` 同理（query 里的词会让它们通过）。  
   → **`ScenarioBatchSuite.DriveTrack_MafRunsTheAgent_ThenScores` 里的 `EvalChecks.NonEmpty(1)` 是一条假绿判据。**
2. **`HarnessConversation` 多加了 user 消息。** MAF 自己会补一条 query 消息，  
   我方 `ToAgentResponse` / `FromTrace` 又 `new ChatMessage(ChatRole.User, query)`，于是重复。  
   **修法（已于 2026-10-14 落地）**：我方只放 `assistant` / `tool` 消息，user 消息交给 MAF。
   `HarnessConversation` 位于**测试工程**（`Fixtures/HarnessUnderTest.cs`），不是 `src/`。  
   修完 `Response` 才等于真答复，`NonEmpty` / `ContainsExpected` / `KeywordCheck` 才恢复区分度。
   > 影响面：现有 `LedgerChecks` 全部只读 `Query`，**不受影响**；  
   > 只有用 `EvalChecks.*` 文本判据的那一条受影响。  
   > 回归闸：`AgentResponse_LeavesUserMessageToMaf`（我方转换结果不得含 user 消息）+
   > `NonEmptyCheck_HasTeeth_EmptyResponseFails`（空答复必红）。AgentTask 54/54 绿。

### 4.2 `EvalChecks` 的名字会骗人

`EvalChecks.ContainsExpected` 比的是 `Response`（含 query 的拼接文本），  
不是「助手回答是否包含金标」。用它做金标比对会**系统性放宽**。  
金标比对应走 族 D（`expectedOutput` 参数 + `ExpectedToolCall`），语义才是对的。

---

## 5. 现在就能拆掉的轮子（未动手，待拍板）

| # | 现在怎么做                         | MAF 里现成的                                                  | 收益                                 |
| - | ----------------------------- | --------------------------------------------------------- | ---------------------------------- |
| 1 | 自己写循环跑 N 次比一致性                | `numRepetitions`                                          | 直接得到「稳定性」维度                        |
| 2 | 只有 `AllPassed` 一个出口           | `AssertNoFailedItems`                                     | 「失败」与「错误」分开表达                      |
| 3 | 「期望调用某工具」写成 check 函数          | `expectedToolCalls` + `ExpectedToolCall`                  | 期望值变成数据，判据零代码                      |
| 4 | 「回答必须包含金标」写成 check            | `expectedOutput` + `EvalChecks.ContainsExpected`（需先修 4.1） | 同上                                 |
| 5 | 整条任务轨迹无判分载体                   | `ConversationSplitters.Full`                              | 「整条任务是否服务原始请求」现成                   |
| 6 | ModelQuality 与 AgentTask 各跑各的 | `MeaiEvaluatorAdapter`                                    | **两库合流**：度量与判定进同一个容器（回答「两个模块是否多余」） |
| 7 | 手写 `EvalCheck` lambda         | `FunctionEvaluator.Create` ×4                             | 语义更直白（收益小）                         |
| 8 | 「任务做完了吗」无判据                   | `LoopEvaluator` 家族（除 `AIJudgeLoopEvaluator`）              | 完成度判定现成（需先做 §8 实验）                 |

**不能拆、必须自己造的**：账本判据（`LedgerChecks`，MAF 不知道我们的 schema）、  
真 HTTP 端到端、并发时序编排、架构扫描、字段级 P/R/F1。

---

## 6. 勘误（本文更正两处既有结论）

| 位置                  | 原结论                                  | 更正                                                                                                                                               |
| ------------------- | ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `MAF判定轨-测试方案.md` §0 | 「MAF 只出三样东西：批量驱动、断言容器、聚合」            | 实际 **28 个类型 / 6 族**，其中 4 族完全未用（族 D/E/F + `MeaiEvaluatorAdapter`）                                                                                 |
| 会话中的口述              | 「MEA.NLP 只有 BLEU/GLEU，无 F1Evaluator」 | **错。**&#x4E;LP 包有 `BLEUEvaluator` / `GLEUEvaluator` / **`F1Evaluator`**（+ 各自 `Context` 类型）——三个都是非 LLM。`F1Evaluator` 至今**没有测试**，而它最贴合「要素抽取有金标」的场景 |

---

## 7. 非目标（本次盘查不动的东西）

- **不动 `src/` 一行**。§4.1 的 `HarnessConversation` 修法已于 2026-10-14 落地，
  且它本身就在**测试工程**内（此前误标为 src 侧）——「不动 src」这条红线始终未被触碰。
- **不引入 Foundry**。§3.2 的字段是范围说明，不是待办。
- **不采用 `AIJudgeLoopEvaluator`**，撞《标准》§1.1 与 §6。
- **不新建 NuGet 依赖**：以上全部类型已经过 `AgentHarness` 的传递依赖到手。

---

## 8. 建议的下一步（按性价比排序）

1. **修 §4.1**（3 行改动）——它现在制造假绿，优先级最高。
2. **把 `EvaluateAsync` 用满**：`expectedOutput` / `expectedToolCalls` 通道（族 D）+ `AssertNoFailedItems`。
3. **给 `F1Evaluator` 补测试**（ModelQuality），并试接 `MeaiEvaluatorAdapter` ——一次打通「两库合流」这条主线。
4. **`LoopAgent` 只做实验不做替换**（先出数据，见《评测实现指南》§8）。

---

## 9. 附：两库分工的准确定义

常见说法是「MEA 提供通用评估机制，MAF 把机制接入 Agent 评估」。  
**方向对，但前提不成立**：MAF 不是接入层，它自己有**一整套独立机制**（族 B 的判据原语 +  
`LocalEvaluator` + 断言出口），全链路不依赖 MEA 也能跑。三处需要修正：

**修正 1｜MAF 借的不是「机制」，是三样具体东西。**  
MAF 公开签名里出现的 MEA 类型只有三个：`IEvaluator`（插件口）、`ChatConfiguration`（裁判模型配置）、  
`EvaluationResult`（结果承载类型）。**度量的实现 MAF 一个都不带**——BLEU / GLEU / F1 及各评分器全在  
MEA 及其卫星包。所以是「借接口 + 借结果模型」，不是「借机制」。

**修正 2｜MAF 甚至不需要 MEA 概念就能判分。**  
实测的 `LocalEvaluator` 路径下，判据全部由 `EvalCheck` 委托完成；MEA 只出现在**返回值**与  
**适配器**两处。反向也成立：MEA 不依赖任何 Agent 概念——ModelQuality 的 4 条用例里没有  
Agent 对象，输入只有 `ChatMessage[]` + `ChatResponse`。

**修正 3｜「循环终止」这块根本不是评估。**  
`LoopAgent` / `LoopEvaluator` / `LoopEvaluation`（族 F）是**运行时控制**——跑的**过程中**  
决定要不要再来一轮，不是跑完的事后评估。它与 MEA 毫无关系。  
「MAF = 把 MEA 机制接入 Agent 评估」这个框子装不下它。

### 准确表述

> **MEA 定「一次评估的结果长什么样」**：metric / rating / interpretation / `IEvaluator` 抽象 + 一组度量实现。  
> **MAF 定「一次任务级评估怎么组织」**：条目建模（`EvalItem`）、批量驱动、断言出口、预期数据通道，  
> 并在需要时把 MEA 的评估器当插件插进来。
>
> 关系不是「机制 vs 接入」，而是「**词汇表 vs 编排**」，而且**双向**：MAF 连判分结果的承载  
> 都借用 MEA 的 `EvaluationResult`。`MeaiEvaluatorAdapter` 这个双向转接件的存在本身就是证据——  
> 若 MAF 只是接入层，它不需要适配器。

### 对本仓库的直接含义


- 分工线画在「**结果表达**」与「**运行组织**」之间，**不是**「通用 vs Agent」。
- 因此 `AgentTask`（MAF / 编排判定）与 `ModelQuality`（MEA / 纯度量）**分置是成立的，不是重复**。
- 唯一真正的合流点是 `MeaiEvaluatorAdapter` 这一个适配器；
  真正可能冗余的是**两边的批量与汇报代码**，不是两个工程本身。

---

## 10. 七条能力清单逐条核对（2026-10-14 二次核实）

对照「① 数据模型 ② 本地评估器 ③ 编排 ④ 切分 ⑤ 工作流与子 Agent ⑥ 提供方集成 ⑦ 结果汇总」逐条实测：

| # | 清单表述 | 结论 | 必须补的限定 |
| - | -------- | ---- | ------------ |
| 1 | `EvalItem` 承载对话/问题/回答/期望输出/期望工具/上下文/工具定义 | ✅ 准确 | 字段真实存在（**11 个属性、3 个构造**）；但 **`Response` 不是「回答」**，是整条拼接文本（见 §4.1） |
| 2 | `LocalEvaluator` / `EvalChecks` / `FunctionEvaluator`，无需外部 API | ✅ 准确 | 另含 `IAgentEvaluator`；`EvalChecks` 共 **7 个原语**；文本类原语被 query 污染（§4.2），「关键词检查」需先修 §4.1 |
| 3 | `AIAgent.EvaluateAsync()` 串起批量 / 重复 / 已有回答评估 | ✅ 准确 | **5 个重载、两条轨**：回放轨（喂预录结果）+ 驱动轨（MAF 自己跑）；`numRepetitions` 只在驱动轨 |
| 4 | `IConversationSplitter` / `ConversationSplitters`，末轮 / 整条 / 逐轮 | ⚠️ **半准** | 内置**只有 `LastTurn` / `Full` 两个**；**「逐轮」不在 `ConversationSplitters`**——另有静态 `EvalItem.PerTurnItems(...)`，或自定义 `IConversationSplitter` |
| 5 | 工作流整体输出 + 各子 Agent 交互的分层结果 | ✅ 准确 | 在**工作流包**里：`WorkflowEvaluationExtensions.EvaluateAsync(Run, evaluator, includeOverall, includePerAgent, …)`；分层结果 = `AgentEvaluationResults.SubResults`。**注意**「按 Agent 分层」(`SubResults`) 与「按评估器分层」(`PerEvaluator`，Foundry-only) **不是一回事** |
| 6 | 可接 Foundry 云端评估器；可用 MEA 质量/安全评估器；可本地+云端组合 | ⚠️ **有前提** | **`Microsoft.Agents.AI.Foundry.FoundryEvals` 不是现有包的公开类型**（`T:` 条目数 = 0，只在文档正文以「例如」被引用）→ 需另装 provider 包；MEA 质量/安全评估器需加 `evaluation.quality` / `evaluation.safety` 卫星包（**本机只缓存了 `evaluation` + `evaluation.nlp`**），且它们几乎全是 LLM-as-judge，撞《标准》§1.1 / §6。「组合」结构上成立（`MeaiEvaluatorAdapter`） |
| 7 | `AgentEvaluationResults` 汇总 + `AssertAllPassed()` 做门禁 | ✅ 准确 | `Total` / `Passed` / `Failed` = **条目数**不是分数；本地有牙的只有 `AssertAllPassed` / `AssertNoFailedItems`，`AssertScoreAtLeast` / `AssertDimensionScoreAtLeast` 实测 **no-op**（§3.3） |

**一句话**：七条里 **5 条准确、1 条半准（④）、1 条有前提（⑥）**。而且所有「准确」背后都有一层限定——
**API 形状在，但本地路径下要么语义被污染（① ②）、要么产出为空（⑥ ⑦）**。
清单描述的是 **API 形状**，不是**本地可用性**；两者不等。

**本次新得（此前盘查遗漏）**：

1. **工作流包的评估面**（`Microsoft.Agents.AI.Workflows.WorkflowEvaluationExtensions`）——§1 的「28 类型 / 6 族」
   只覆盖 `Microsoft.Agents.AI` 核心，未纳入工作流包，故此前把 `SubResults` 误当 Foundry-only。
2. **`FoundryEvals` 非公开类型**——「可接 Foundry」是**缝留好了、provider 没带**。
3. **族 E 只有两个内置切分器**——「逐轮」另有出处（`EvalItem.PerTurnItems`）。
